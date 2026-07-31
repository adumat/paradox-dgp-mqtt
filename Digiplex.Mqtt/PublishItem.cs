namespace Digiplex.Mqtt;

/// <summary>A single queued MQTT publish: topic, payload, retain flag.</summary>
public readonly record struct PublishItem(string Topic, string Payload, bool Retain);
