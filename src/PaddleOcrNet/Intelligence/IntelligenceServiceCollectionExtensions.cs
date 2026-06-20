using Microsoft.Extensions.DependencyInjection;
using PaddleOcrNet.Services;

namespace PaddleOcrNet.Intelligence;

/// <summary>
/// Dependency-injection helpers for the document-intelligence layer (LLM-backed key-information extraction
/// and document Q&amp;A). These compose with <see cref="ServiceCollectionExtensions.AddPaddleOcrNet"/>: register
/// the OCR service, register an <see cref="IChatModel"/> (the built-in OpenAI-compatible adapter or your own),
/// then register <see cref="IDocumentIntelligence"/>.
/// </summary>
public static class IntelligenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers a caller-supplied <see cref="IChatModel"/> instance as a singleton — the bring-your-own-provider
    /// path. Use this to plug in any LLM by implementing <see cref="IChatModel"/> yourself.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="chatModel">The chat model to register.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddChatModel(this IServiceCollection services, IChatModel chatModel)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(chatModel);
        services.AddSingleton(chatModel);
        return services;
    }

    /// <summary>
    /// Registers the built-in <see cref="OpenAiCompatibleChatModel"/> as the singleton <see cref="IChatModel"/>.
    /// Works with OpenAI, Azure OpenAI, Ollama, vLLM, LM Studio, Groq, Together, DeepSeek, Mistral, and any other
    /// OpenAI-style <c>/chat/completions</c> endpoint — build <paramref name="options"/> directly or via a
    /// factory such as <see cref="OpenAiCompatibleOptions.OpenAi"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The endpoint / auth / model configuration.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddOpenAiCompatibleChatModel(
        this IServiceCollection services, OpenAiCompatibleOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton<IChatModel>(_ => new OpenAiCompatibleChatModel(options));
        return services;
    }

    /// <summary>
    /// Registers <see cref="IDocumentIntelligence"/> (implemented by <see cref="DocumentIntelligenceEngine"/>) as
    /// a singleton. Requires an <see cref="IPaddleOcrService"/> (from
    /// <see cref="ServiceCollectionExtensions.AddPaddleOcrNet"/>) and an <see cref="IChatModel"/>
    /// (from <see cref="AddOpenAiCompatibleChatModel"/> or <see cref="AddChatModel"/>) to be registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">Optional engine options (temperature, vision, prompt overrides, structure pass-through).</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddPaddleOcrDocumentIntelligence(
        this IServiceCollection services, DocumentIntelligenceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IDocumentIntelligence>(sp =>
        {
            var ocr = sp.GetRequiredService<IPaddleOcrService>();
            var chat = sp.GetRequiredService<IChatModel>();
            return new DocumentIntelligenceEngine(ocr, chat, options);
        });
        return services;
    }
}
