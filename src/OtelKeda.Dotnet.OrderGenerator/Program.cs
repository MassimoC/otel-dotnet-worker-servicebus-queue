using Bogus;
using OtelKeda.Dotnet.Contracts;
using Newtonsoft.Json;
using System;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Serilog;
using System.Collections.Generic;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Resources;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using System.Diagnostics;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry;

namespace OtelKeda.Dotnet.OrderGenerator
{
    class Program
    {
        private const string QueueName = "orders";
        private const string ConnectionString = "Endpoint=sb://otelkeda.servicebus.windows.net/;SharedAccessKeyName=order-publisher;SharedAccessKey=TODO;EntityPath=orders";
        private const string OLTP_ENDPOINT = "Otlp:Endpoint";
        private const string OLTP_SERVICE = "Otlp:ServiceName";
        private const string OLTP_LOGSPATH = "Otlp:LogsPath";
        private const string OLTP_ACTIVITYSOURCE = "Otlp:ActivitySource";

        private static IConfiguration Configuration { get; set; }
        private static ActivitySource Activity;
        private static readonly TextMapPropagator Propagator = new TraceContextPropagator();


        static async Task Main(string[] args)
        {

            Configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .AddEnvironmentVariables()
                .AddCommandLine(args)
                .Build();

            Activity = new(Configuration.GetValue<string>(OLTP_ACTIVITYSOURCE));

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(Configuration)
                .WriteTo.OpenTelemetry(options =>
                {
                    options.Protocol = Serilog.Sinks.OpenTelemetry.OtlpProtocol.Grpc;
                    options.Endpoint = $"{Configuration.GetValue<string>(OLTP_ENDPOINT)}{Configuration.GetValue<string>(OLTP_LOGSPATH)}";
                    options.ResourceAttributes = new Dictionary<string, object>
                    {
                        ["service.name"] = Configuration.GetValue<string>(OLTP_SERVICE)
                    };
                })
                .CreateLogger();


            //using var host = CreateHostBuilder(args).Build().Run();
            using (var host = CreateHostBuilder(args).Build())
            {
                await host.StartAsync();
                var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

                Console.WriteLine("Let's queue some orders, how many do you want?");

                var requestedAmount = DetermineOrderAmount();
                await QueueOrders(requestedAmount);

                Console.WriteLine("That's it, see you later!");

                lifetime.StopApplication();
                await host.WaitForShutdownAsync();
            }

        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                config.AddEnvironmentVariables();
            })
            .ConfigureServices((hostBuilderContext, services) =>
            {
                Action<ResourceBuilder> resourceBuilder = resource => resource
                        .AddService(Configuration.GetValue<string>(OLTP_SERVICE), serviceVersion: "0.22.0")
                        .AddTelemetrySdk();

                services.AddOpenTelemetry()
                    .ConfigureResource(resourceBuilder)
                    .WithTracing(tracerProviderBuilder => tracerProviderBuilder
                        .AddSource(Configuration.GetValue<string>(OLTP_ACTIVITYSOURCE))
                        //.SetSampler(new AlwaysOnSampler())
                        .AddOtlpExporter(options => options.Endpoint = new Uri(Configuration.GetValue<string>(OLTP_ENDPOINT)))
                    );

            }).UseSerilog();

        private static async Task QueueOrders(int requestedAmount)
        {
            var serviceBusClient = new ServiceBusClient(ConnectionString);
            var serviceBusSender = serviceBusClient.CreateSender(QueueName);

            for (int currentOrderAmount = 0; currentOrderAmount < requestedAmount; currentOrderAmount++)
            {
                var order = GenerateOrder();
                var rawOrder = JsonConvert.SerializeObject(order);
                var orderMessage = new ServiceBusMessage(rawOrder);

                Baggage.SetBaggage("Sent by", Configuration.GetValue<string>(OLTP_SERVICE));

                using (var activity = Activity.StartActivity("Azure Servicebus Publish", ActivityKind.Producer))
                {
                    // Use the Propagator to add trace context to message properties
                    Propagators.DefaultTextMapPropagator.Inject(new(activity.Context, Baggage.Current), orderMessage.ApplicationProperties,
                        (qProps, key, value) =>
                        {
                            qProps ??= new Dictionary<string, object>();
                            qProps[key] = value;
                        });

                    AddTagsToTrace(activity);

                    Console.WriteLine($"Queuing order {order.Id} - A {order.ArticleNumber} for {order.Customer.FirstName} {order.Customer.LastName}");
                    await serviceBusSender.SendMessageAsync(orderMessage);
                }

            }
        }

        private static Order GenerateOrder()
        {
            var customerGenerator = new Faker<Customer>()
                .RuleFor(u => u.FirstName, (f, u) => f.Name.FirstName())
                .RuleFor(u => u.LastName, (f, u) => f.Name.LastName());

            var orderGenerator = new Faker<Order>()
                .RuleFor(u => u.Customer, () => customerGenerator)
                .RuleFor(u => u.Id, f => Guid.NewGuid().ToString())
                .RuleFor(u => u.Amount, f => f.Random.Int())
                .RuleFor(u => u.ArticleNumber, f => f.Commerce.Product());

            return orderGenerator.Generate();
        }

        private static int DetermineOrderAmount()
        {
            var rawAmount = Console.ReadLine();
            if (int.TryParse(rawAmount, out int amount))
            {
                return amount;
            }

            Console.WriteLine("That's not a valid amount, let's try that again");
            return DetermineOrderAmount();
        }

        private static void AddTagsToTrace(Activity activity)
        {
            activity?.SetTag("messaging.system", "azureservicebus");
            activity?.SetTag("messaging.destination_kind", "queue");
            activity?.SetTag("messaging.azureservicebus.queue_name", QueueName); 
        }
    }

}
