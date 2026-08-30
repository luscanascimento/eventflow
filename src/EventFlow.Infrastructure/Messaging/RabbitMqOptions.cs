namespace EventFlow.Infrastructure.Messaging;

/// <summary>
/// Configuration options for connecting to RabbitMQ and configuring AMQP topology.
/// </summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMQ";

    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string ExchangeName { get; set; } = "eventflow.events";
    public string QueueName { get; set; } = "eventflow.events.processing";
    public string RoutingKeyPattern { get; set; } = "#";
    public string DlxExchangeName { get; set; } = "eventflow.dlx";
    public string DlqQueueName { get; set; } = "eventflow.events.dlq";
    public ushort PrefetchCount { get; set; } = 10;
    public string ClientProvidedName { get; set; } = "eventflow-service";
    public int RequestedHeartbeatSeconds { get; set; } = 60;
}
