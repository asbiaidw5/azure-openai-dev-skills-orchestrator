using Elsa.Extensions;
using Elsa.Workflows.Core;
using Elsa.Workflows.Core.Attributes;
using Elsa.Workflows.Core.Models;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Orchestration;

using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AI.DevTeam.Skills;
using Microsoft.SemanticKernel.Connectors.AI.OpenAI;


namespace Elsa.SemanticKernel;

/// <summary>
/// Invoke a Semantic Kernel skill.
/// </summary>
[Activity("Elsa", "Semantic Kernel", "Invoke a Semantic Kernel skill. ", DisplayName = "Generic Semantic Kernel Skill", Kind = ActivityKind.Task)]
[PublicAPI]
public class SemanticKernelSkill : CodeActivity<string>
{
    [Input(
    Description = "System Prompt",
    UIHint = InputUIHints.MultiLine,
    DefaultValue = PromptDefaults.SystemPrompt)]
    public Input<string> SysPrompt { get; set; } = default!;

    [Input(
    Description = "User Input Prompt",
    UIHint = InputUIHints.MultiLine,
    DefaultValue = PromptDefaults.UserPrompt)]
    public Input<string> Prompt { get; set; }

    [Input(
    Description = "Max retries",
    UIHint = InputUIHints.SingleLine,
    DefaultValue = KernelSettings.DefaultMaxRetries)]
    public Input<int> MaxRetries { get; set; }

    [Input(
    Description = "The skill to invoke from the semantic kernel",
    UIHint = InputUIHints.SingleLine,
    DefaultValue = "Chat")]
    public Input<string> SkillName { get; set; }

    [Input(
    Description = "The function to invoke from the skill",
    UIHint = InputUIHints.SingleLine,
    DefaultValue = "ChatCompletion")]
    public Input<string> FunctionName { get; set; }

    /*     [Input(
            Description = "Mockup - don't actually call the AI, just output the prompts",
            UIHint = InputUIHints.Checkbox,
            DefaultValue = false)]
        public Input<bool> Mockup { get; set; } */

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext workflowContext)
    {
        var test = SkillName.Get(workflowContext);
        var skillName = SkillName.Get(workflowContext);
        var functionName = FunctionName.Get(workflowContext);
        var systemPrompt = SysPrompt.Get(workflowContext);
        var maxRetries = MaxRetries.Get(workflowContext);
        var prompt = Prompt.Get(workflowContext);
        //var mockup = Mockup.Get(workflowContext);
        var mockup = false;

        string info = ($"#################\nSkill: {skillName}\nFunction: {functionName}\nPrompt: {prompt}\n#################\n\n");

        if (mockup)
        {
            workflowContext.SetResult(info);
        }
        else
        {
            // get the kernel
            var kernel = KernelBuilder();

            // load the skill
            var promptTemplate = Skills.ForSkillAndFunction(skillName, functionName);

            var function = kernel.CreateSemanticFunction(promptTemplate, new OpenAIRequestSettings { MaxTokens = 8000, Temperature = 0.4, TopP = 1 });

            // set the context (our prompt)
            var contextVars = new ContextVariables();
            contextVars.Set("input", prompt);

            /*         var interestingMemories = kernel.Memory.SearchAsync("ImportedMemories", prompt, 2);
                    var wafContext = "Consider the following contextual snippets:";
                    await foreach (var memory in interestingMemories)
                    {
                        wafContext += $"\n {memory.Metadata.Text}";
                    } */


            //context.Set("wafContext", wafContext);

            var answer = await kernel.RunAsync(contextVars, function);
            workflowContext.SetResult(answer);
        }
    }

    /// <summary>
    /// Load the skills into the kernel
    /// </summary>
    private string ListSkillsInKernel(IKernel kernel)
    {

        var theSkills = LoadSkillsFromAssemblyAsync("skills", kernel);
        SKContext context = kernel.CreateNewContext();
        var functionsAvailable = context.Functions.GetFunctionViews();

        var list = new StringBuilder();
        foreach (var function in functionsAvailable)
        {
            Console.WriteLine($"Skill: {function.PluginName}");
            
                // Function description
                if (function.Description != null)
                {
                    list.AppendLine($"// {function.Description}");
                }
                else
                {
                    Console.WriteLine("{0}.{1} is missing a description", function.PluginName, function.Name);
                    list.AppendLine($"// Function {function.PluginName}.{function.Name}.");
                }

                // Function name
                list.AppendLine($"{function.PluginName}.{function.Name}");

                // Function parameters
                foreach (var p in function.Parameters)
                {
                    var description = string.IsNullOrEmpty(p.Description) ? p.Name : p.Description;
                    var defaultValueString = string.IsNullOrEmpty(p.DefaultValue) ? string.Empty : $" (default value: {p.DefaultValue})";
                    list.AppendLine($"Parameter \"{p.Name}\": {description} {defaultValueString}");
                }
        }

        Console.WriteLine($"List of all skills ----- {list.ToString()}");
        return list.ToString();
    }

    /// <summary>
    /// Gets a semantic kernel instance
    /// </summary>
    /// <returns>Microsoft.SemanticKernel.IKernel</returns>
    private IKernel KernelBuilder()
    {
        var kernelSettings = KernelSettings.LoadSettings();

        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(kernelSettings.LogLevel ?? LogLevel.Warning);
        });

        /* 
        var memoryStore = new QdrantMemoryStore(new QdrantVectorDbClient("http://qdrant", 1536, port: 6333));
        var embedingGeneration = new AzureTextEmbeddingGeneration(kernelSettings.EmbeddingDeploymentOrModelId, kernelSettings.Endpoint, kernelSettings.ApiKey);
        var semanticTextMemory = new SemanticTextMemory(memoryStore, embedingGeneration);
        */

        var kernel = new KernelBuilder()
        .WithLoggerFactory(loggerFactory)
        .WithAzureChatCompletionService(kernelSettings.DeploymentOrModelId, kernelSettings.Endpoint, kernelSettings.ApiKey, true, kernelSettings.ServiceId, true)
        //.WithMemory(semanticTextMemory)
        .Build();

        return kernel;
    }

    ///<summary>
    /// Gets a list of the skills in the assembly
    ///</summary>
    private IEnumerable<string> LoadSkillsFromAssemblyAsync(string assemblyName, IKernel kernel)
    {
        var skills = new List<string>();
        var assembly = Assembly.Load(assemblyName);
        Type[] skillTypes = assembly.GetTypes().ToArray();
        foreach (Type skillType in skillTypes)
        {
            if (skillType.Namespace.Equals("Microsoft.SKDevTeam"))
            {
                skills.Add(skillType.Name);
                var functions = skillType.GetFields();
                foreach (var function in functions)
                {
                    string field = function.FieldType.ToString();
                    if (field.Equals("Microsoft.SKDevTeam.SemanticFunctionConfig"))
                    {
                        var prompt = Skills.ForSkillAndFunction(skillType.Name, function.Name);
                        var skfunc = kernel.CreateSemanticFunction(
                            prompt, new OpenAIRequestSettings { MaxTokens = 8000, Temperature = 0.4, TopP = 1 });

                        Console.WriteLine($"SK Added function: {skfunc.SkillName}.{skfunc.Name}");
                    }
                }
            }
        }
        return skills;
    }
}