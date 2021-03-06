#tool nuget:?package=GitVersion.CommandLine
#tool nuget:?package=gitlink
#tool nuget:?package=vswhere
#addin nuget:?package=Cake.Incubator
#addin nuget:?package=Cake.Git

var sln = new FilePath("src/Xablu.Adal.sln");
var outputDir = new DirectoryPath("artifacts");
var nuspecDir = new DirectoryPath("nuspec");
var target = Argument("target", "Default");

var isRunningOnAppVeyor = AppVeyor.IsRunningOnAppVeyor;

Task("Clean").Does(() =>
{
    CleanDirectories("./**/bin");
    CleanDirectories("./**/obj");
	CleanDirectories(outputDir.FullPath);

	EnsureDirectoryExists(outputDir);
});

GitVersion versionInfo = null;
Task("Version").Does(() => {
	versionInfo = GitVersion(new GitVersionSettings {
		UpdateAssemblyInfo = true,
		OutputType = GitVersionOutput.Json
	});

	Information("GitVersion -> {0}", versionInfo.Dump());
});

Task("UpdateAppVeyorBuildNumber")
	.IsDependentOn("Version")
    .WithCriteria(() => isRunningOnAppVeyor)
    .Does(() =>
{
    AppVeyor.UpdateBuildVersion(versionInfo.FullBuildMetaData);
});

FilePath msBuildPath;
Task("ResolveBuildTools")
	.Does(() => 
{
	var vsLatest = VSWhereLatest();
	msBuildPath = (vsLatest == null)
		? null
		: vsLatest.CombineWithFilePath("./MSBuild/15.0/Bin/MSBuild.exe");
});

Task("Restore")
	.IsDependentOn("ResolveBuildTools")
	.Does(() => {
	NuGetRestore(sln, new NuGetRestoreSettings {
		ToolPath = "tools/nuget.exe"
	});
	// MSBuild(sln, settings => settings.WithTarget("Restore"));
});

Task("Build")
	.IsDependentOn("ResolveBuildTools")
	.IsDependentOn("Clean")
	.IsDependentOn("UpdateAppVeyorBuildNumber")
	.IsDependentOn("Restore")
	.Does(() =>  {

	var settings = new MSBuildSettings 
	{
		Configuration = "Release",
		ToolPath = msBuildPath
	};

	settings.Properties.Add("DebugSymbols", new List<string> { "True" });
	settings.Properties.Add("DebugType", new List<string> { "Full" });

	MSBuild(sln, settings);
});

Task("GitLink")
	.IsDependentOn("Build")
	//pdbstr.exe and costura are not xplat currently
	.WithCriteria(() => IsRunningOnWindows())
	.WithCriteria(() => 
		StringComparer.OrdinalIgnoreCase.Equals(versionInfo.BranchName, "develop") || 
		IsMasterOrReleases())
	.Does(() => 
{
    GitLink(sln.GetDirectory(), 
        new GitLinkSettings {
            RepositoryUrl = "https://github.com/Xablu/Xablu.ADAL",
            ArgumentCustomization = args => args.Append("-ignore apiclient.sample")
        });
});

Task("Package")
    .IsDependentOn("GitLink")
    .Does(() => 
{
    var nugetSettings = new NuGetPackSettings {
		Authors = new [] { "Xablu" },
		Owners = new [] { "Xablu" },
		IconUrl = new Uri("https://raw.githubusercontent.com/Xablu/Xablu.Adal/master/icon_xablu.png"),
		ProjectUrl = new Uri("https://github.com/Xablu/Xablu.Adal"),
		LicenseUrl = new Uri("https://github.com/Xablu/Xablu.Adal/blob/master/LICENSE"),
		Copyright = "Copyright (c) Xablu",
		RequireLicenseAcceptance = false,
		Version = versionInfo.NuGetVersion,
		Symbols = false,
		NoPackageAnalysis = true,
		OutputDirectory = outputDir,
		Verbosity = NuGetVerbosity.Detailed,
		BasePath = "./nuspec"
    };

    EnsureDirectoryExists(outputDir);

    var nuspecs = new List<string> {
        "Xablu.Adal.nuspec"
    };

    foreach(var nuspec in nuspecs)
    {
        NuGetPack(nuspecDir + "/" + nuspec, nugetSettings);
    }
});

Task("PublishPackages")
    .IsDependentOn("Package")
    .WithCriteria(() => !BuildSystem.IsLocalBuild)
    .WithCriteria(() => IsRepository("Xablu/Xablu.ADAL"))
    .WithCriteria(() => 
		StringComparer.OrdinalIgnoreCase.Equals(versionInfo.BranchName, "develop") || 
		IsMasterOrReleases())
    .Does (() =>
{
	if (StringComparer.OrdinalIgnoreCase.Equals(versionInfo.BranchName, "master") && !IsTagged())
    {
        Information("Packages will not be published as this release has not been tagged.");
        return;
    }

	// Resolve the API key.
	var nugetKeySource = GetNugetKeyAndSource();
	var apiKey = nugetKeySource.Item1;
	var source = nugetKeySource.Item2;

	Information("Search for nuget packages in: " + outputDir);
	var nugetFiles = GetFiles(outputDir + "/*.nupkg");

	foreach(var nugetFile in nugetFiles)
	{
    	NuGetPush(nugetFile, new NuGetPushSettings {
            Source = source,
            ApiKey = apiKey
        });
	}
});

Task("UploadAppVeyorArtifact")
	.IsDependentOn("Package")
	.WithCriteria(() => !AppVeyor.Environment.PullRequest.IsPullRequest)
	.WithCriteria(() => isRunningOnAppVeyor)
	.Does(() => {

	Information("Artifacts Dir: {0}", outputDir.FullPath);

	foreach(var file in GetFiles(outputDir.FullPath + "/*")) {
		Information("Uploading {0}", file.FullPath);
		AppVeyor.UploadArtifact(file.FullPath);
	}
});

Task("Default")
	.IsDependentOn("PublishPackages")
	.IsDependentOn("UploadAppVeyorArtifact")
	.Does(() => 
{
});

RunTarget(target);

bool IsMasterOrReleases()
{
	if (StringComparer.OrdinalIgnoreCase.Equals(versionInfo.BranchName, "master"))
		return true;

	if (versionInfo.BranchName.Contains("releases/"))
		return true;

	return false;
}

bool IsRepository(string repoName)
{
	if (isRunningOnAppVeyor)
	{
		var buildEnvRepoName = AppVeyor.Environment.Repository.Name;
		Information("Checking repo name: {0} against build repo name: {1}", repoName, buildEnvRepoName);
		return StringComparer.OrdinalIgnoreCase.Equals(repoName, buildEnvRepoName);
	}
	else
	{
		try
		{
			var path = MakeAbsolute(new DirectoryPath("./")).FullPath;
			using (var repo = new LibGit2Sharp.Repository(path))
			{
				var origin = repo.Network.Remotes.FirstOrDefault(
					r => r.Name.ToLowerInvariant() == "origin");
				return origin.Url.ToLowerInvariant() == 
					"https://github.com/" + repoName.ToLowerInvariant();
			}
		}
		catch(Exception ex)
		{
			Information("Failed to lookup repository: {0}", ex);
			return false;
		}
	}
}

bool IsTagged()
{
	var path = MakeAbsolute(new DirectoryPath("./")).FullPath;
	using (var repo = new LibGit2Sharp.Repository(path))
	{
		var head = repo.Head;
		var headSha = head.Tip.Sha;
		
		var tag = repo.Tags.FirstOrDefault(t => t.Target.Sha == headSha);
		if (tag == null)
		{
			Information("HEAD is not tagged");
			return false;
		}

		Information("HEAD is tagged: {0}", tag.FriendlyName);
		return true;
	}
}

Tuple<string, string> GetNugetKeyAndSource()
{
	var apiKeyKey = string.Empty;
	var sourceKey = string.Empty;
	if (isRunningOnAppVeyor)
	{
		apiKeyKey = "NUGET_APIKEY";
		sourceKey = "NUGET_SOURCE";
	}
	else
	{
		if (StringComparer.OrdinalIgnoreCase.Equals(versionInfo.BranchName, "develop"))
		{
			apiKeyKey = "NUGET_APIKEY_DEVELOP";
			sourceKey = "NUGET_SOURCE_DEVELOP";
		}
		else if (IsMasterOrReleases())
		{
			apiKeyKey = "NUGET_APIKEY_MASTER";
			sourceKey = "NUGET_SOURCE_MASTER";
		}
	}

	var apiKey = EnvironmentVariable(apiKeyKey);
	if (string.IsNullOrEmpty(apiKey))
		throw new Exception(string.Format("The {0} environment variable is not defined.", apiKeyKey));

	var source = EnvironmentVariable(sourceKey);
	if (string.IsNullOrEmpty(source))
		throw new Exception(string.Format("The {0} environment variable is not defined.", sourceKey));

	return Tuple.Create(apiKey, source);
}