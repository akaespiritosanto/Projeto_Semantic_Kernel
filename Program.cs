// Import packages
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;

var builder = Kernel.CreateBuilder()
    .AddOllamaChatCompletion(
        modelId: "llama3.1:latest",
        endpoint: new Uri("http://localhost:11434")
    );

// Entterprise Components
builder.Services.AddLogging(x => x.AddConsole().SetMinimumLevel(LogLevel.Trace));

var app = builder.Build();

var chatCompletionService = app.GetRequiredService<IChatCompletionService>();

string? input;
do
{
    Console.Write("User > ");
    input = Console.ReadLine();

    var result = await chatCompletionService.GetChatMessageContentAsync(input, kernel: app);

    Console.WriteLine("Assistant > " + result);

} while (input is not null);