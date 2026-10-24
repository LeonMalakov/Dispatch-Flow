using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DispatchFlow.Application.Orders;
using DispatchFlow.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace DispatchFlow.IntegrationTests.Api;

public sealed class OrderErrorTests {
    private const string INTERNAL_ERROR = "Simulated failure: SELECT * FROM secret_orders; Password=not-a-real-password";

    [Theory]
    [InlineData("Development", "POST", "application/json")]
    [InlineData("Development", "GET", "application/json")]
    [InlineData("Production", "POST", "application/json")]
    [InlineData("Production", "GET", "application/json")]
    [InlineData("Development", "POST", "text/html")]
    [InlineData("Development", "GET", "text/html")]
    [InlineData("Production", "POST", "text/html")]
    [InlineData("Production", "GET", "text/html")]
    [InlineData("Development", "CANCEL", "application/json")]
    [InlineData("Production", "CANCEL", "application/json")]
    [InlineData("Development", "CANCEL", "text/html")]
    [InlineData("Production", "CANCEL", "text/html")]
    public async Task UnexpectedRepositoryFailure_IsLoggedAndReturnsSafe500(string environment, string method, string accept) {
        // Arrange.
        var repository = Substitute.For<IOrderRepository>();

        repository
            .CreateWithReservationAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<CreateOrderResult>(new InvalidOperationException(INTERNAL_ERROR)));
        repository
            .GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<Order?>(new InvalidOperationException(INTERNAL_ERROR)));
        repository
            .CancelWithReleaseAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<CancelOrderResult>(new InvalidOperationException(INTERNAL_ERROR)));

        using var logs = new ExceptionLogCollector();

        await using var factory = new TestApiFactory(
            "Host=127.0.0.1;Database=unused;Username=unused",
            environment,
            false,
            services => {
                services.RemoveAll<IOrderRepository>();
                services.AddScoped<IOrderRepository>(_ => repository);
                services.AddLogging(logging => logging.AddProvider(logs));
            });

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));

        // Act.
        using var response = method switch {
            "POST" => await client.PostAsJsonAsync("/api/orders", new { slotId = Guid.NewGuid(), quantity = 1 }),
            "GET" => await client.GetAsync($"/api/orders/{Guid.NewGuid()}"),
            "CANCEL" => await client.PostAsync($"/api/orders/{Guid.NewGuid()}/cancel", null),
            _ => throw new ArgumentOutOfRangeException(nameof(method))
        };

        // Assert.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(INTERNAL_ERROR, body);
        Assert.DoesNotContain("secret_orders", body);
        Assert.DoesNotContain("Password", body);
        Assert.DoesNotContain(nameof(InvalidOperationException), body);
        Assert.DoesNotContain("stackTrace", body);

        if (accept == "application/json") {
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = Assert.IsType<ProblemDetails>(await response.Content.ReadFromJsonAsync<ProblemDetails>());

            Assert.Equal(500, problem.Status);

            using var json = JsonDocument.Parse(body);
            Assert.False(json.RootElement.TryGetProperty("exception", out _));
        }

        Assert.Contains(logs.Exceptions, exception => exception.Message == INTERNAL_ERROR);
    }

    private sealed class ExceptionLogCollector : ILoggerProvider {
        private readonly ConcurrentQueue<Exception> _exceptions = new();

        public IEnumerable<Exception> Exceptions => _exceptions;

        public ILogger CreateLogger(string categoryName) => new ExceptionLogger(_exceptions);

        public void Dispose() { }

        private sealed class ExceptionLogger(ConcurrentQueue<Exception> exceptions) : ILogger {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) {
                if (logLevel >= LogLevel.Error && exception is not null) {
                    exceptions.Enqueue(exception);
                }
            }
        }
    }
}
