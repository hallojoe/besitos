using System.Text.Json;
using Besitos.Worker;

namespace Besitos.Email;

public static class EmailHandlerDescription
{
    public static HandlerDescriptor Descriptor { get; } = new(EmailBatch.Command, "Send email batch",
        "Sends a separate SMTP email to each recipient, using one connection per batch. A batch permits up to 100 recipients.")
    {
        PayloadMediaType = "application/json",
        PayloadSchema = JsonSerializer.Deserialize<JsonElement>("""
            {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object",
             "required":["subject","text","recipients"],"properties":{
               "subject":{"type":"string","minLength":1,"maxLength":200},
               "text":{"type":"string","maxLength":16000},
               "html":{"type":["string","null"],"maxLength":16000},
               "recipients":{"type":"array","minItems":1,"maxItems":100,"items":{"type":"string","format":"email","maxLength":254}}}}
            """),
        ExamplePayload = """{"subject":"Test receipt","text":"Thank you.","recipients":["test@example.test"]}""",
        DuplicateHandling = DuplicateHandling.None,
        DuplicateHandlingNotes = "Retries resend the whole batch and can duplicate accepted emails. SMTP acceptance does not prove final delivery."
    };
}
