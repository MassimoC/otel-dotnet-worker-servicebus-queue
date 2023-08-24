using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using System.Collections.Generic;
using OtelKeda.Dotnet.Contracts;
using System;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace OtelKeda.Dotnet.OrderProcessor
{
    internal class Program
    {
        private static IConfiguration Configuration { get; set; }

        private const string OLTP_ENDPOINT = "Otlp:Endpoint";
        private const string OLTP_SERVICE = "Otlp:ServiceName";
        private const string OLTP_LOGSPATH = "Otlp:LogsPath";
        

        public static void Main(string[] args)
        {

            Configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .AddEnvironmentVariables()
                .AddCommandLine(args)
                .Build();

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

            CreateHostBuilder(args).Build().Run();
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
                        .AddTelemetrySdk()
                        .AddService(Configuration.GetValue<string>(OLTP_SERVICE), serviceVersion: "0.21.0");

                    services.AddOpenTelemetry()
                        .ConfigureResource(resourceBuilder)
                        .WithTracing(tracerProviderBuilder => tracerProviderBuilder
                            .AddSource("OtelKeda")
                            .AddConsoleExporter()
                            .AddOtlpExporter(options => options.Endpoint = new Uri(Configuration.GetValue<string>(OLTP_ENDPOINT)))
                        );

                    services.AddHostedService<OrdersQueueProcessor>();
                }).UseSerilog();
                   
    }
}
