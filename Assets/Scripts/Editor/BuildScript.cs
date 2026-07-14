using UnityEditor;
using UnityEditor.Build.Reporting;
using System;

public class BuildScript
{
    public static void BuildiOS()
    {
        string outputPath = GetArg("-outputPath") ?? "ios_build";

        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/SampleScene.unity" },
            locationPathName = outputPath,
            target = BuildTarget.iOS,
            options = BuildOptions.Development
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);

        if (report.summary.result != BuildResult.Succeeded)
            throw new Exception($"Unity build failed: {report.summary.result}");

        Console.WriteLine($"Build succeeded: {outputPath}");
    }

    private static string GetArg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
