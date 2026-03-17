// Import packages
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

// Populate values from your OpenAI deployment
var modelId = "gpt-4o-mini";
var endpoint = "https://api.openai.com/v1/chat/completions";
var apiKey = "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";

// Create kernel
var builder = Kernel.CreateBuilder();
builder.AddOpenAIChatCompletion(modelId, endpoint, apiKey);

builder.Services.AddLogging(services => services.AddConsole().SetMinimumLevel(LogLevel.Trace));

Kernel kernel = builder.Build();

// Retrieve the chat completion service
var chatCompletionService = kernel.Services.GetRequiredService<IChatCompletionService>();

// Add the plugin to the kernel
kernel.Plugins.AddFromType<LightsPlugin>("Lights");

OpenAIPromptExecutionSettings openAIPromptExecutionSettings = new()
{
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
};

// Create chat history
var history = new ChatHistory();

// Get the response from the AI
var result = await chatCompletionService.GetChatMessageContentAsync(
    history,
    executionSettings: openAIPromptExecutionSettings,
    kernel: kernel
);

