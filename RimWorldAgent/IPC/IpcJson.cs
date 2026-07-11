using System;
using System.IO;
using System.Text.Json;
using RimWorldAgent.IPC.Generated;

namespace RimWorldAgent.IPC
{
    internal static class IpcJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            // The IPC schema treats optional object fields as omitted, not null.
            // This is required for the envelope meta field on net472/System.Text.Json.
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public static IpcEnvelope Create<T>(string type, string? requestId, T payload)
        {
            return new IpcEnvelope
            {
                Type = type,
                RequestId = requestId,
                Payload = JsonSerializer.SerializeToElement(payload, Options)
            };
        }

        public static T DeserializePayload<T>(IpcEnvelope envelope)
        {
            if (!envelope.Payload.HasValue)
                throw new InvalidDataException("IPC message payload is missing.");
            return envelope.Payload.Value.Deserialize<T>(Options)
                ?? throw new InvalidDataException("IPC message payload is null.");
        }

        public static string Serialize(IpcEnvelope envelope)
            => JsonSerializer.Serialize(envelope, Options);

        public static IpcEnvelope Deserialize(string line)
        {
            var envelope = JsonSerializer.Deserialize<IpcEnvelope>(line, Options)
                ?? throw new InvalidDataException("IPC message is null.");
            if (!string.Equals(envelope.Protocol, "rimworld-agent-ipc", StringComparison.Ordinal) || envelope.Version != 1)
                throw new InvalidDataException("Unsupported IPC protocol or version.");
            if (string.IsNullOrWhiteSpace(envelope.Type))
                throw new InvalidDataException("IPC message type is missing.");
            return envelope;
        }

    }
}
