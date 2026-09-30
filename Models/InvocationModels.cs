namespace DubboTester.Models;

public enum InvocationRoute
{
    Registry,
    DirectProvider
}

public sealed class InvocationInput
{
    public InvocationRoute Route { get; set; } = InvocationRoute.Registry;
    public string ServiceName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 20_880;
    public string RequestJson { get; set; } = "{}";
    public string JavaParameterTypes { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string AttachmentsJson { get; set; } = string.Empty;

    public InvocationInput Clone() => (InvocationInput)MemberwiseClone();
}

public sealed class InvocationOutput
{
    public bool Success { get; init; }
    public string Content { get; init; } = string.Empty;
    public string? Error { get; init; }
    public int RequestElapsed { get; init; }
    public long? RequestId { get; init; }
    public byte? ResponseStatus { get; init; }
    public byte? SerializationId { get; init; }
    public IReadOnlyDictionary<string, object>? Attachments { get; init; }
}

public sealed class InvocationHistory
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public bool Favorite { get; set; }
    public bool Success { get; set; }
    public InvocationRoute Route { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string RequestJson { get; set; } = string.Empty;
    public string JavaParameterTypes { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string AttachmentsJson { get; set; } = string.Empty;
    public string Response { get; set; } = string.Empty;
    public int ElapsedMs { get; set; }

    public string Endpoint => $"{ServiceName}#{MethodName}";
}
