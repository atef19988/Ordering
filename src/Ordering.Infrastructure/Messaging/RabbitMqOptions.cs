namespace Ordering.Infrastructure.Messaging;

/// <summary>Bound from the <c>RabbitMq</c> section. Defaults match <c>docker-compose.yml</c> (development only).</summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5672;

    public string User { get; set; } = "guest";

    public string Password { get; set; } = "guest";

    public string VirtualHost { get; set; } = "/";
}
