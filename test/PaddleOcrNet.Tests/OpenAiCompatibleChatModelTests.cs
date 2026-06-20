using System.Net;
using System.Text;
using System.Text.Json;
using PaddleOcrNet.Intelligence;
using Xunit;

namespace PaddleOcrNet.Tests;

/// <summary>
/// Unit tests for <see cref="OpenAiCompatibleChatModel"/> using a fake <see cref="HttpMessageHandler"/> — no
/// network. They lock the request wire-shape (roles, model, json_object, vision data-URL parts, endpoint/auth)
/// and the response mapping (text, usage, finish reason, error handling).
/// </summary>
public class OpenAiCompatibleChatModelTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _responseBody;

        public CapturingHandler(string responseBody, HttpStatusCode status = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _status = status;
        }

        public string? CapturedBody { get; private set; }
        public Uri? CapturedUri { get; private set; }
        public string? AuthHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedUri = request.RequestUri;
            if (request.Headers.TryGetValues("Authorization", out var values))
                AuthHeader = string.Join(",", values);
            if (request.Content is not null)
                CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private const string SuccessJson =
        "{\"model\":\"test-model\",\"choices\":[{\"message\":{\"role\":\"assistant\"," +
        "\"content\":\"hello world\"},\"finish_reason\":\"stop\"}]," +
        "\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":7,\"total_tokens\":18}}";

    private static OpenAiCompatibleChatModel Model(CapturingHandler handler) =>
        new(new HttpClient(handler), OpenAiCompatibleOptions.Generic("https://example.test/v1", "secret-key", "test-model"));

    private static ChatRequest Req(params ChatMessage[] messages) => new() { Messages = messages };

    [Fact]
    public async Task Parses_text_usage_model_and_finish_reason()
    {
        var handler = new CapturingHandler(SuccessJson);
        var resp = await Model(handler).CompleteAsync(Req(ChatMessage.User("hi")));

        Assert.Equal("hello world", resp.Text);
        Assert.Equal("stop", resp.FinishReason);
        Assert.Equal("test-model", resp.Model);
        Assert.NotNull(resp.Usage);
        Assert.Equal(11, resp.Usage!.PromptTokens);
        Assert.Equal(7, resp.Usage.CompletionTokens);
        Assert.Equal(18, resp.Usage.TotalTokens);
    }

    [Fact]
    public async Task Request_maps_roles_model_override_and_max_tokens()
    {
        var handler = new CapturingHandler(SuccessJson);
        await Model(handler).CompleteAsync(new ChatRequest
        {
            Model = "override-model",
            Messages = new[] { ChatMessage.System("sys"), ChatMessage.User("usr") },
            MaxTokens = 256,
        });

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var root = doc.RootElement;
        Assert.Equal("override-model", root.GetProperty("model").GetString());
        Assert.Equal(256, root.GetProperty("max_tokens").GetInt32());
        var msgs = root.GetProperty("messages");
        Assert.Equal(2, msgs.GetArrayLength());
        Assert.Equal("system", msgs[0].GetProperty("role").GetString());
        Assert.Equal("sys", msgs[0].GetProperty("content").GetString());
        Assert.Equal("user", msgs[1].GetProperty("role").GetString());
        Assert.Equal("usr", msgs[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task JsonMode_sets_response_format_json_object()
    {
        var handler = new CapturingHandler(SuccessJson);
        await Model(handler).CompleteAsync(new ChatRequest { Messages = new[] { ChatMessage.User("x") }, JsonMode = true });

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.Equal("json_object", doc.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Without_JsonMode_response_format_is_omitted()
    {
        var handler = new CapturingHandler(SuccessJson);
        await Model(handler).CompleteAsync(Req(ChatMessage.User("x")));

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        Assert.False(doc.RootElement.TryGetProperty("response_format", out _));
    }

    [Fact]
    public async Task Image_attachment_becomes_data_url_content_part()
    {
        var handler = new CapturingHandler(SuccessJson);
        var image = new ChatImage(new byte[] { 1, 2, 3 }, "image/png");
        await Model(handler).CompleteAsync(Req(ChatMessage.User("see this", new[] { image })));

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("see this", content[0].GetProperty("text").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal(
            "data:image/png;base64," + Convert.ToBase64String(new byte[] { 1, 2, 3 }),
            content[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task Endpoint_appends_chat_completions_and_sends_bearer_auth()
    {
        var handler = new CapturingHandler(SuccessJson);
        await Model(handler).CompleteAsync(Req(ChatMessage.User("x")));

        Assert.Equal("https://example.test/v1/chat/completions", handler.CapturedUri!.ToString());
        Assert.Equal("Bearer secret-key", handler.AuthHeader);
    }

    [Fact]
    public async Task Non_success_status_throws_ChatModelException_with_status()
    {
        var handler = new CapturingHandler("{\"error\":\"slow down\"}", HttpStatusCode.TooManyRequests);
        var ex = await Assert.ThrowsAsync<ChatModelException>(
            () => Model(handler).CompleteAsync(Req(ChatMessage.User("x"))));
        Assert.Equal(429, ex.StatusCode);
    }

    [Fact]
    public void Azure_factory_configures_api_key_header_and_version_query()
    {
        var opts = OpenAiCompatibleOptions.AzureOpenAi("https://my.openai.azure.com", "gpt4o", "secret");
        Assert.Equal("api-key", opts.ApiKeyHeaderName);
        Assert.Equal(string.Empty, opts.ApiKeyPrefix);
        Assert.Equal("gpt4o", opts.DefaultModel);

        var uri = opts.ResolveEndpoint().ToString();
        Assert.Contains("/openai/deployments/gpt4o/chat/completions", uri);
        Assert.Contains("api-version=", uri);
    }

    [Fact]
    public void Ollama_factory_is_keyless()
    {
        var opts = OpenAiCompatibleOptions.Ollama("llama3.2-vision");
        Assert.Null(opts.ApiKey);
        Assert.Equal("http://localhost:11434/v1/chat/completions", opts.ResolveEndpoint().ToString());
    }
}
