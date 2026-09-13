namespace Contracts;

// RabbitMQ queue names. Each service listens on exactly one; replies go back to "orders".
public static class Queues
{
    public const string Orders = "orders";
    public const string Payments = "payments";
    public const string Inventory = "inventory";
    public const string Shipping = "shipping";
    public const string Notifications = "notifications";

    // Fanout exchange for integration events that live outside the saga.
    public const string OrderEvents = "order-events";
}
