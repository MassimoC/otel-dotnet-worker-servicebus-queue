using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace OtelKeda.Dotnet.OrderProcessor
{
    public abstract class QueueWorker<TMessage> : BackgroundService
    {
        private const string OLTP_ACTIVITYSOURCE = "Otlp:ActivitySource";

        protected ILogger<QueueWorker<TMessage>> Logger { get; }
        protected IConfiguration Configuration { get; }
        private static ActivitySource Activity;

        protected QueueWorker(IConfiguration configuration, ILogger<QueueWorker<TMessage>> logger)
        {
            Configuration = configuration;
            Logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var queueName = Configuration.GetValue<string>("KEDA_SERVICEBUS_QUEUE_NAME");
            Activity = new(Configuration.GetValue<string>(OLTP_ACTIVITYSOURCE));

            Logger.LogInformation($"Queue name :: {queueName}");
            var messageProcessor = CreateServiceBusProcessor(queueName);
            messageProcessor.ProcessMessageAsync += HandleMessageAsync;
            messageProcessor.ProcessErrorAsync += HandleReceivedExceptionAsync;
            
            Logger.LogInformation($"Starting message pump on queue {queueName} in namespace {messageProcessor.FullyQualifiedNamespace}");
            await messageProcessor.StartProcessingAsync(stoppingToken);
            Logger.LogInformation("Message pump started");

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            Logger.LogInformation("Closing message pump");
            await messageProcessor.CloseAsync(cancellationToken: stoppingToken);
            Logger.LogInformation("Message pump closed : {Time}", DateTimeOffset.UtcNow);
        }

        private ServiceBusProcessor CreateServiceBusProcessor(string queueName)
        {
            var serviceBusClient = AuthenticateToAzureServiceBus();
            var messageProcessor = serviceBusClient.CreateProcessor(queueName);
            return messageProcessor;
        }

        private ServiceBusClient AuthenticateToAzureServiceBus()
        {
            var authenticationMode = Configuration.GetValue<AuthenticationMode>("KEDA_SERVICEBUS_AUTH_MODE");
            
            ServiceBusClient serviceBusClient;

            switch (authenticationMode)
            {
                case AuthenticationMode.ConnectionString:
                    Logger.LogInformation($"Authentication by using connection string");
                    serviceBusClient = ServiceBusClientFactory.CreateWithConnectionStringAuthentication(Configuration);
                    break;
                case AuthenticationMode.ServicePrinciple:
                    Logger.LogInformation("Authentication by using service principle");
                    serviceBusClient = ServiceBusClientFactory.CreateWithServicePrincipleAuthentication(Configuration);
                    break;
                case AuthenticationMode.PodIdentity:
                    Logger.LogInformation("Authentication by using pod identity");
                    serviceBusClient = ServiceBusClientFactory.CreateWithPodIdentityAuthentication(Configuration, Logger);
                    break;
                case AuthenticationMode.WorkloadIdentity:
                    Logger.LogInformation("Authentication by using workload identity");
                    serviceBusClient = ServiceBusClientFactory.CreateWithWorkloadIdentityAuthentication(Configuration, Logger);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            return serviceBusClient;
        }

        private async Task HandleMessageAsync (ProcessMessageEventArgs processMessageEventArgs)
        {
            try
            {

                // Use the Propagator to extract the context from message properties
                var parentContext = Propagators.DefaultTextMapPropagator.Extract(default, processMessageEventArgs.Message.ApplicationProperties,
                    (qProps, key) =>
                    {
                        if (!qProps.TryGetValue(key, out var value) || value?.ToString() is null)
                        {
                            return Enumerable.Empty<string>();
                        }
                        return new[] { value.ToString() };
                    });

                // Copy the parent span baggage to the current span baggage
                Baggage.Current = parentContext.Baggage;


                using (var activity = Activity.StartActivity("Consume Message", ActivityKind.Consumer, parentContext.ActivityContext))
                {

                    var rawMessageBody = Encoding.UTF8.GetString(processMessageEventArgs.Message.Body.ToBytes().ToArray());
                    Logger.LogInformation("Received message {MessageId} with body {MessageBody}",
                        processMessageEventArgs.Message.MessageId, rawMessageBody);

                    var order = JsonConvert.DeserializeObject<TMessage>(rawMessageBody);
                    if (order != null)
                    {
                        string result = await ProcessMessage(order, processMessageEventArgs.Message.MessageId,
                            processMessageEventArgs.Message.ApplicationProperties,
                            processMessageEventArgs.CancellationToken);

                        activity?.SetTag("process.orderarticle", result);
                    }
                    else
                    {
                        Logger.LogError(
                            "Unable to deserialize to message contract {ContractName} for message {MessageBody}",
                            typeof(TMessage), rawMessageBody);
                    }

                    Logger.LogInformation("Message {MessageId} processed", processMessageEventArgs.Message.MessageId);

                    await processMessageEventArgs.CompleteMessageAsync(processMessageEventArgs.Message);
                }

            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unable to handle message");
            }
        }

        private Task HandleReceivedExceptionAsync(ProcessErrorEventArgs exceptionEvent)
        {
            Logger.LogError(exceptionEvent.Exception, "Unable to process message");
            return Task.CompletedTask;
        }

        protected abstract Task<string> ProcessMessage(TMessage order, string messageId, IReadOnlyDictionary<string, object> userProperties, CancellationToken cancellationToken);
    }
}
