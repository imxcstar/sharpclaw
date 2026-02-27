using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace sharpclaw.Clients;

/// <summary>
/// 在 OpenAI 兼容 API 请求体中注入自定义字段。
/// </summary>
public class ExtraFieldsPolicy(Dictionary<string, JsonElement> fields) : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        InjectFields(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        InjectFields(message);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private void InjectFields(PipelineMessage message)
    {
        if (message.Request.Content is null) return;

        using var stream = new MemoryStream();
        message.Request.Content.WriteTo(stream);
        var json = JsonNode.Parse(stream.ToArray());
        if (json is not JsonObject obj) return;

        foreach (var (key, value) in fields)
            obj[key] = JsonNode.Parse(value.GetRawText());

        message.Request.Content = System.ClientModel.BinaryContent.Create(
            BinaryData.FromString(obj.ToJsonString()));
    }
}
