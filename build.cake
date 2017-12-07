//////////////////////////////////////////////////////////////////////
// TOOLS
//////////////////////////////////////////////////////////////////////

using Path = System.IO.Path;
using IO = System.IO;
using System.Text.RegularExpressions;
using Cake.Common.Tools;

//////////////////////////////////////////////////////////////////////
// ARGUMENTS
//////////////////////////////////////////////////////////////////////
var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");

///////////////////////////////////////////////////////////////////////////////
// GLOBAL VARIABLES
///////////////////////////////////////////////////////////////////////////////
var localPackagesDir = "../LocalPackages";
var buildDir = @".\build";
var unpackFolder = Path.Combine(buildDir, "temp");
var unpackFolderFullPath = Path.GetFullPath(unpackFolder);
var artifactsDir = @".\artifacts";
var files = new string[] { "AWSCLI64.msi", "AWSCLI32.msi" };
var file = string.Empty;
var nugetVersion = string.Empty;
var nugetPackageFile = string.Empty;

///////////////////////////////////////////////////////////////////////////////
// SETUP / TEARDOWN
///////////////////////////////////////////////////////////////////////////////
Setup(context =>
{
    
});

Teardown(context =>
{
    Information("Finished running tasks.");
});

//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////

Task("Clean")
    .Does(() =>
{
    CleanDirectory(buildDir);
    CleanDirectory(unpackFolder);
    CleanDirectory(artifactsDir);
});

// Task("Restore-NuGet-Packages")
//     .IsDependentOn("Clean")
//     .Does(() =>
// {
//     NuGetRestore("./src/Example.sln");
// });

Task("Restore-Source-Package")
    .IsDependentOn("Clean")
    .Does(() => 
{
    var outputPath = File($"{buildDir}/{file}");
    var url = $"https://s3.amazonaws.com/aws-cli/{file}";
    Information($"Downloading {url}");
    DownloadFile(url, outputPath);
});

Task("Unpack-Source-Package")
    .IsDependentOn("Restore-Source-Package")
    .IsDependentOn("Clean")
    .Does(() => 
{
    var sourcePackage = file;
    
    Information($"Unpacking {sourcePackage}");
    
    var processArgumentBuilder = new ProcessArgumentBuilder();
    processArgumentBuilder.Append($"/a {sourcePackage}");
    processArgumentBuilder.Append("/qn");
    processArgumentBuilder.Append($"TARGETDIR={unpackFolderFullPath}");
    var processSettings = new ProcessSettings { Arguments = processArgumentBuilder, WorkingDirectory = buildDir };
    StartProcess("msiexec.exe", processSettings);
    Information($"Unpacked {sourcePackage} to {unpackFolderFullPath}");
});

Task("GetVersion")
    .IsDependentOn("Unpack-Source-Package")
    .Does(() => 
{
    Information("Determining version number");
    Information(System.IO.Directory.GetCurrentDirectory());

    var cliDir = Path.Combine(unpackFolderFullPath, "Amazon", "AWSCLI");
    //Information(cliDir);
    var processArgumentBuilder = new ProcessArgumentBuilder();
    processArgumentBuilder.Append("--version");
    var processSettings = new ProcessSettings 
                            { 
                                Arguments = processArgumentBuilder, 
                                WorkingDirectory = Directory(cliDir), 
                                RedirectStandardOutput = true,
                                RedirectStandardError = true
                            };
    IEnumerable<string> standardOutput;
    IEnumerable<string> errorOutput;

    StartProcess(Path.Combine(cliDir, "aws.exe"), processSettings, out standardOutput, out errorOutput);
    var outputLine = errorOutput.First();
    Information($"Version output is \"{outputLine}\"");
    var regexMatch = Regex.Match(outputLine, @"aws-cli\/(?<Version>[\d\.]*)");
    nugetVersion = regexMatch.Groups["Version"].Value;
    Information($"Calculated version number: {nugetVersion}");

    if(BuildSystem.IsRunningOnTeamCity)
        BuildSystem.TeamCity.SetBuildNumber(nugetVersion);
});

Task("Pack")
    .IsDependentOn("GetVersion")
    .Does(() =>
{
    var fileWithoutExtension = Path.GetFileNameWithoutExtension(file);
    Information($"Building Octopus.Dependencies.{fileWithoutExtension} v{nugetVersion}");
    
    NuGetPack("awscli.nuspec", new NuGetPackSettings {
        BasePath = unpackFolder,
        OutputDirectory = artifactsDir,
        ArgumentCustomization = args => args.Append($"-Properties \"version={nugetVersion};subpackagename={fileWithoutExtension}\"")
    });
});

Task("Publish")
    .WithCriteria(BuildSystem.IsRunningOnTeamCity)
    .IsDependentOn("Pack")
    .Does(() => 
{
    var fileWithoutExtension = Path.GetFileNameWithoutExtension(file);
    NuGetPush($"{artifactsDir}/Octopus.Dependencies.{fileWithoutExtension}.{nugetVersion}.nupkg", new NuGetPushSettings {
        Source = "https://octopus.myget.org/F/octopus-dependencies/api/v3/index.json",
        ApiKey = EnvironmentVariable("MyGetApiKey")
    });
});

Task("CopyToLocalPackages")
    .WithCriteria(BuildSystem.IsLocalBuild)
    .IsDependentOn("Pack")
    .Does(() => 
{
    var fileWithoutExtension = Path.GetFileNameWithoutExtension(file);
    CreateDirectory(localPackagesDir);
    CopyFileToDirectory(Path.Combine(artifactsDir, $"Octopus.Dependencies.{fileWithoutExtension}.{nugetVersion}.nupkg"), localPackagesDir);
});


//////////////////////////////////////////////////////////////////////
// TASK TARGETS
//////////////////////////////////////////////////////////////////////

Task("FullChain")
    .IsDependentOn("Clean")
    .IsDependentOn("Restore-Source-Package")
    .IsDependentOn("Unpack-Source-Package")
    .IsDependentOn("GetVersion")
    .IsDependentOn("Pack")
    .IsDependentOn("Publish")
    .IsDependentOn("CopyToLocalPackages");

Task("Default").Does(() => 
{  
    foreach (var f in files)
    {
        file = f;
        RunTarget("FullChain");
    }
});

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////

RunTarget(target);