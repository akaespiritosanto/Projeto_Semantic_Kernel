using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using OpenAI.Realtime;
using DotNetEnv;
using Microsoft.VisualBasic;
using semantic_kernel.Dtos;
using Microsoft.SemanticKernel.Agents.Orchestration.Sequential;
using Microsoft.SemanticKernel.Agents.Runtime.Core;
using Microsoft.SemanticKernel.Services;

var builder = Kernel.CreateBuilder()
    .AddOllamaChatCompletion(
        modelId: "llama3.1:latest",
        endpoint: new Uri("http://localhost:11434")
    );

Env.Load();

var kernel = builder.Build();


string? apiKey = Environment.GetEnvironmentVariable("API_KEY");

string baseAddress = "https://api.openweathermap.org/data/2.5/weather";

var ideasAgent = new ChatCompletionAgent
{
    Name = "Ideas",
    Instructions =
        "You are a Madeira island tourist guide." +
        "Your job is to give 1 place ideias to visit or enjoy in Madeira island based on the location in the user input.\n" +
        "The results output should display the name of the place, the location and coordinates (decimal units). \n" +
        "The final output have to be exacly like this: name_of_the_place location lat lon",
    Kernel = kernel
};

var weatherAgent = new ChatCompletionAgent{
    Name = "Weather",
    Instructions =
        "You are a weather specialist agent." +
        "Your job is to give the correct information about the weather (based in a weather api) in a specific place (in Madeira Island) to InterpreterAgent.\n" +
        "The results output should display the current maximum/minimum temperature and the current temperature",
    Kernel = kernel
};

var interpreterAgent = new ChatCompletionAgent
{
    Name = "Interpreter",
    Instructions =
        "You are an text interpreter specialist." +
        "Your job is to decide 1 place that user should visit or enjoy based on the temperature informations given by the weatherAgent and what the user wants.\n" +
        "The results output should display the name of the place, the location, coordinates (decimal units) and the current temperature given by the weatherAgent",
    Kernel = kernel
};

string ideasOutput = "";
string weatherOutput = "";
string interpreterOutput = "";

do
{
    Console.Write("User > ");
    string? userInput = System.Console.ReadLine();

    ideasOutput = "";
    weatherOutput = "";
    interpreterOutput = "";
    
    await foreach (var response in ideasAgent.InvokeAsync(userInput))
    {
        ideasOutput += response.Message.Content;
    }
    await foreach (var response in weatherAgent.InvokeAsync(ideasOutput))
    {
        weatherOutput += response.Message.Content;
    }
    await foreach (var response in interpreterAgent.InvokeAsync(weatherOutput))
    {
        interpreterOutput += response.Message.Content;
    }

    Console.WriteLine("Assistant > " + interpreterOutput);
} while (true);


