using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Magentic;
using Microsoft.SemanticKernel.Agents.Orchestration;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using OpenAI.Realtime;
using DotNetEnv;
using Microsoft.VisualBasic;
using semantic_kernel.Dtos;

var builder = Kernel.CreateBuilder()
    .AddOllamaChatCompletion(
        modelId: "llama3.1:latest",
        endpoint: new Uri("http://localhost:11434")
    );

Env.Load();

var kernel = builder.Build();

string? apiKey = Environment.GetEnvironmentVariable("API_KEY");

string baseAddress = "https://api.openweathermap.org/data/2.5/weather";

var weatherAgent = new ChatCompletionAgent
{
    Name = "Weather",
    Instructions =
        "You are a weather specialist agent." +
        "Your job is to give the correct information about the weather (based in a weather api) in a specific place (in Madeira Island) to IdeasAgent.\n" +
        "If there is not any information about the weather in some place just say: There is no information available in the api about the weather in that place.\n" +
        "The results output should display the current maximum/minimum temperature and the current temperature",
    Kernel = kernel
};

var ideasAgent = new ChatCompletionAgent
{
    Name = "Ideas",
    Instructions =
        "You are a Madeira island tourist guide." +
        "Your job is to give place ideias to visit or enjoy in Madeira island based on the weather information given by WeatherAgent.\n" +
        "If you can not provide any information in the output just say: there is no information available about the (location or coordinates).\n" +
        "The results output should display the name of the place, the location and coordinates",
    Kernel = kernel

};


Console.Write("User > ");
string? input =  System.Console.ReadLine();
