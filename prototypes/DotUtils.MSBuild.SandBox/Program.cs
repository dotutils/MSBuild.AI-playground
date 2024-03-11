using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.AI.ChatCompletion;
using Microsoft.SemanticKernel.AI.Embeddings;
using Microsoft.SemanticKernel.Connectors.AI.OpenAI.TextEmbedding;
using System;
using System.Globalization;
using System.Text;
using Microsoft.Build.Logging;
using Microsoft.Build.Framework;
using System.Threading;
using Microsoft.DotNet.Interactive.AIUtilities;
using HandlebarsDotNet;

namespace DotUtils.MSBuild.SandBox;

internal class Program
{
    static void SetCreds()
    {
        //
        // You need: key, endpoint, aiModel, embeddings model
        //
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://<your-oai-deployment-name>.openai.azure.com/");
        Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", "<api key>");
        Environment.SetEnvironmentVariable("AZURE_OPENAI_MODEL", "BinlogPlayground01-gpt");
        Environment.SetEnvironmentVariable("AZURE_OPENAI_EMBEDDINGS_MODEL", "BinlogPlayground01-embeddings");
    }

    static async Task Main(string[] args)
    {
        //
        // Env setup (Azure) and credentials (set in env, rather then in code).
        //
        SetCreds();

        // 
        // Embeddings generation - run just once per binlog
        //
        string ranksFileName = @"buildlink-ranks.txt";
        try
        {
            await BinlogToEmbeddingsFile(Path.Combine(GetResourcesDir(), @"buildlink-pack.binlog"), ranksFileName);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }

        //
        // The query loop. Run once embeddings are generated.
        //
        try
        {
            await QueryBinlog(ranksFileName);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    static string GetResourcesDir([System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
        => Path.Combine(Path.GetDirectoryName(sourceFilePath), "resources");

    static List<Tuple<ReadOnlyMemory<float>, string>> ReadEmbeddings(string filePath)
    {
        List<Tuple<ReadOnlyMemory<float>, string>> result = new();

        StringReader reader = new(File.ReadAllText(filePath));
        StringBuilder recordBuilder = new();
        do
        {
            string? line = reader.ReadLine();
            if (string.IsNullOrEmpty(line))
            {
                return result;
            }
            var vector = ParseFloats(line);
            while (!(line = reader.ReadLine())!.Equals(EmbedingsSeparator))
            {
                recordBuilder.AppendLine(line);
            }

            result.Add(new(vector, recordBuilder.ToString()));
            recordBuilder.Clear();
        } while (true);
        

        ReadOnlyMemory<float> ParseFloats(string line)
        {
            return line.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => float.Parse(s, CultureInfo.InvariantCulture))
                .ToArray();
        }
    }

    static IEnumerable<string> TokenizeBinlog(string binlogPath)
    {

        //BinaryLogReplayEventSource source = new();
        //source.AnyEventRaised += (sender, eventArgs) => {
        //    yield return EventArgsToString(eventArgs);
        //};
        //source.Replay(binlogPath);

        using var binaryReader = BinaryLogReplayEventSource.OpenReader(binlogPath);
        int fileFormatVersion = binaryReader.ReadInt32();
        using var reader = new BuildEventArgsReader(binaryReader, fileFormatVersion);

        Dictionary<int, string> projects = new();
        Dictionary<string, Dictionary<int, string>> projectTragets = new();
        Dictionary<string, Dictionary<string, Dictionary<int, string>>> tragetTasks = new();

        while (reader.Read() is { } instance)
        {
            yield return EventArgsToString(instance);
        }

        string EventArgsToString(BuildEventArgs eventArgs)
        {
            string project = string.Empty;
            string target = string.Empty;
            string task = string.Empty;
            string prefix = string.Empty;

            switch (eventArgs)
            {
                case ProjectStartedEventArgs prj:
                {
                    projects[prj.BuildEventContext.ProjectContextId] = prj.ProjectFile;
                }
                    break;
                case ProjectEvaluationStartedEventArgs prEv:
                {
                    projects[prEv.BuildEventContext.ProjectContextId] = prEv.ProjectFile;
                }
                    break;
                case TargetStartedEventArgs trg:
                {
                    string projName = projects[trg.BuildEventContext.ProjectContextId];
                    if (!projectTragets.ContainsKey(projName))
                    {
                        projectTragets[projName] = new();
                    }

                    projectTragets[projName][trg.BuildEventContext.TargetId] = trg.TargetName;
                }
                    break;
                case TaskStartedEventArgs tsk:
                {
                    string projName = projects[tsk.BuildEventContext.ProjectContextId];
                    string targetName = projectTragets[projName][tsk.BuildEventContext.TargetId];

                    if (!tragetTasks.ContainsKey(projName))
                    {
                        tragetTasks[projName] = new();
                    }

                    if (!tragetTasks[projName].ContainsKey(targetName))
                    {
                        tragetTasks[projName][targetName] = new();
                    }

                    tragetTasks[projName][targetName] ??= new();
                    tragetTasks[projName][targetName][tsk.BuildEventContext.TaskId] = tsk.TaskName;
                }
                    break;
                default:
                    break;
            }

            if (eventArgs.BuildEventContext != null)
            {
                if (projects.TryGetValue(eventArgs.BuildEventContext.ProjectContextId, out project))
                {
                    prefix = $"Project: {project}";
                    if (projectTragets.TryGetValue(project, out var targets))
                    {
                        if (targets.TryGetValue(eventArgs.BuildEventContext.TargetId, out target))
                        {
                            prefix += $", Target: {target}";
                            if (tragetTasks.TryGetValue(project, out var tasks))
                            {
                                if (tasks.TryGetValue(target, out var tasksPerId))
                                {
                                    if (tasksPerId.TryGetValue(eventArgs.BuildEventContext.TaskId, out task))
                                    {
                                        prefix += $", Task: {task}";
                                    }
                                }
                            }
                        }
                    }

                    prefix += " - ";
                }
            }

            //string prefix = $"Project: {project}, Target: {target}, Task: {task}";

            switch (eventArgs)
            {
                case BuildWarningEventArgs w:
                    return $"{prefix}{w.File}({w.LineNumber},{w.ColumnNumber}): {w.Subcategory} warning {w.Code}: {w.Message}";
                case BuildErrorEventArgs e:
                    return $"{prefix}{e.File}({e.LineNumber},{e.ColumnNumber}): {e.Subcategory} error {e.Code}: {e.Message}";
                default:
                {
                    string msg = eventArgs.Message;
                    if (eventArgs is BuildMessageEventArgs m && m.LineNumber != 0)
                    {
                        msg = $"{m.File}({m.LineNumber},{m.ColumnNumber}): {msg}";
                    }

                    return prefix + msg;
                }
            }

            //var project = GetOrAddProject(args.BuildEventContext.ProjectContextId);
            //var target = project.GetTargetById(args.BuildEventContext.TargetId);
            //var task = target.GetTaskById(args.BuildEventContext.TaskId);
        }
    }

    const string EmbedingsSeparator = $"-------------------------------------------------";

    static async Task BinlogToEmbeddingsFile(string binlogFile, string rankFile)
    {
        if (File.Exists(rankFile))
        {
            return;
        }

        string aoaiEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!;
        string aoaiApiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!;
        string embeddingsModel = Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDINGS_MODEL")!;
        var embeddingGen = new AzureOpenAITextEmbeddingGeneration(embeddingsModel, aoaiEndpoint, aoaiApiKey);
        var tokenizer = await Tokenizer.CreateAsync(TokenizerModel.ada2);

        int cnt = 0;
        int truncatedCnt = 0;
        const int batchSize = 50;
        foreach (string[] inputs in TokenizeBinlog(binlogFile).Chunk(batchSize))
        {
            //TokenizeBinlog(binlogFile).Chunk(batchSize)
            cnt += inputs.Length;
            string[] truncatedInputs = inputs.Select(i => tokenizer.TruncateByTokenCount(i, 8191))
                .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

            for (int i = 0; i < inputs.Length; i++)
            {
                if (inputs[i] != truncatedInputs[i])
                {
                    truncatedCnt++;
                }
            }

            IList<ReadOnlyMemory<float>> exampleEmbeddings;
            try
            {
                exampleEmbeddings = await embeddingGen.GenerateEmbeddingsAsync(truncatedInputs);
            }
            catch (Exception e)
            {
                string possibleErrorInput = null;
                try
                {
                    foreach (string truncatedInput in truncatedInputs)
                    {
                        possibleErrorInput = truncatedInput;
                        await embeddingGen.GenerateEmbeddingsAsync(new[] { possibleErrorInput });
                    }
                }
                catch (Exception exception)
                {
                    Console.WriteLine(possibleErrorInput);
                    Console.WriteLine(exception);
                    throw;
                }

                Console.WriteLine(e);
                throw;
            }
            var rankings = truncatedInputs.Zip(exampleEmbeddings, (input, embedding) => $"{string.Join(';', embedding.Span.ToArray())}{Environment.NewLine}{input}{Environment.NewLine}{EmbedingsSeparator}");
            await File.AppendAllLinesAsync(rankFile, rankings);
            Console.WriteLine($"Total: {cnt}, Truncated: {truncatedCnt}");
        }

        Console.WriteLine("---- DONE ----");
        Console.WriteLine($"Total: {cnt}, Truncated: {truncatedCnt}");
    }

    // Stolen from https://devblogs.microsoft.com/dotnet/demystifying-retrieval-augmented-generation-with-dotnet/
    static async Task Test1()
    {
        string aoaiEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!;
        string aoaiApiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!;
        string aoaiModel = Environment.GetEnvironmentVariable("AZURE_OPENAI_MODEL")!;

        // Initialize the kernel
        IKernel kernel = Kernel.Builder
            .WithAzureOpenAIChatCompletionService(aoaiModel, aoaiEndpoint, aoaiApiKey)
            .Build();

        // Create a new chat
        IChatCompletion ai = kernel.GetService<IChatCompletion>();
        ChatHistory chat = ai.CreateNewChat("You are an AI assistant that helps people find information.");


        // Q&A loop
        while (true)
        {
            Console.Write("Question: ");
            chat.AddUserMessage(Console.ReadLine()!);

            string answer = await ai.GenerateMessageAsync(chat);
            chat.AddAssistantMessage(answer);
            Console.WriteLine(answer);

            Console.WriteLine();
        }
    }

    static async Task QueryBinlog(string embeddingsFilePath)
    {
        string aoaiEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!;
        string aoaiApiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!;
        string aoaiModel = Environment.GetEnvironmentVariable("AZURE_OPENAI_MODEL")!;
        string embeddingsModel = Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDINGS_MODEL")!;

        string systemMessage = $"""
                        You are an AI assistant with expertise in reviewing MSBuild logs to determine build issues and to answer questions related to provided build log entries.
                        You are answering concisely and accurately,
                        """;

        // Initialize the kernel
        IKernel kernel = Kernel.Builder
            .WithAzureOpenAIChatCompletionService(aoaiModel, aoaiEndpoint, aoaiApiKey)
            .Build();

        // Create a new chat
        IChatCompletion ai = kernel.GetService<IChatCompletion>();
        ChatHistory chat = ai.CreateNewChat(systemMessage);
        var embeddingGen = new AzureOpenAITextEmbeddingGeneration(embeddingsModel, aoaiEndpoint, aoaiApiKey);
        StringBuilder contextBuilder = new();

        var embeddings = ReadEmbeddings(embeddingsFilePath);

        // Q&A loop
        while (true)
        {
            Console.Write("Question: ");
            string question = Console.ReadLine()!;

            ReadOnlyMemory<float> questionEmbedding = (await embeddingGen.GenerateEmbeddingsAsync(new[] { question }))[0];

            var topContext = embeddings
                .Select(e => new { score = CosineSimilarity(e.Item1.Span, questionEmbedding.Span), text = e.Item2 })
                .OrderByDescending(e => e.score)
                .Take(10)
                .Select(e => e.text)
                .ToList();

            int contextToRemove = -1;
            foreach (string s in topContext)
            {
                contextBuilder.AppendLine();
                contextBuilder.AppendLine(s);
            }

            if (contextBuilder.Length != 0)
            {
                contextBuilder.Insert(0, "Here's some additional information: ");
                contextToRemove = chat.Count;
                chat.AddUserMessage(contextBuilder.ToString());
            }

            chat.AddUserMessage(question);

            contextBuilder.Clear();

            string answer = await ai.GenerateMessageAsync(chat);
            chat.AddAssistantMessage(answer);
            var color = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(answer);

            if (contextToRemove >= 0) chat.RemoveAt(contextToRemove);
            Console.WriteLine();

            Console.ForegroundColor = color;
        }
    }

    static float CosineSimilarity(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        float dot = 0, xSumSquared = 0, ySumSquared = 0;

        for (int i = 0; i < x.Length; i++)
        {
            dot += x[i] * y[i];
            xSumSquared += x[i] * x[i];
            ySumSquared += y[i] * y[i];
        }

        return dot / (MathF.Sqrt(xSumSquared) * MathF.Sqrt(ySumSquared));
    }
}
