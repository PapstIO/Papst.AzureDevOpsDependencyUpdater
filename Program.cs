using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Spectre.Console;

namespace AzureDevOpsNuGetUpdater
{
  class Program
  {
    static async Task Main(string[] args)
    {
      // Azure DevOps Configurations
      var azureDevOpsUrl = "https://dev.azure.com/papstio";
      var projectName = "Sunbelt%20SCM";
      var personalAccessToken = "pcno324wqdsdt66m5dkjmj5oymcbkrh3iqcx6c4cpl57o62ut4oq";


      var azureDevOpsUri = new Uri(azureDevOpsUrl);
      var credentials = new VssBasicCredential(string.Empty, personalAccessToken);

      var connection = new VssConnection(azureDevOpsUri, credentials);

      var gitClient = connection.GetClient<GitHttpClient>();
      var projectClient = connection.GetClient<ProjectHttpClient>();

      var repositories = await gitClient.GetRepositoriesAsync(projectName);

      var selectedRepositories = await new MultiSelectionPrompt<GitRepository>()
        .AddChoices(repositories)
        .UseConverter(repo => repo.Name)
        .ShowAsync(AnsiConsole.Console, CancellationToken.None);

      foreach (var repo in selectedRepositories)
      {
        Console.WriteLine($"Checking repository: {repo.Name}");

        var items = await gitClient.GetItemsAsync(projectName, repo.Id, scopePath: "/",
          recursionLevel: VersionControlRecursionType.Full,
          versionDescriptor: new GitVersionDescriptor
            { Version = repo.DefaultBranch.Split('/').Last(), VersionType = GitVersionType.Branch });

        var csprojFiles = items.Where(i => i.Path.EndsWith(".csproj")).ToList();
        var directoryPackagesProps = items.FirstOrDefault(i => i.Path.EndsWith("Directory.Packages.props"));

        var packageReferences = new List<PackageReference>();

        foreach (var csproj in csprojFiles)
        {
          var content = await gitClient.GetItemContentAsync(repo.Id, csproj.Path);
          packageReferences.AddRange(ParseCsproj(content));
        }

        if (directoryPackagesProps != null)
        {
          var content = await gitClient.GetItemContentAsync(repo.Id, directoryPackagesProps.Path);
          packageReferences.AddRange(ParseCentralPackageManagement(content));
        }

        var nugetConfig = items.FirstOrDefault(i => i.Path.EndsWith("nuget.config"));
        var packageSourceUris = nugetConfig != null
          ? GetPackageSources(await gitClient.GetItemContentAsync(repo.Id, nugetConfig.Path))
          : new List<string> { "https://api.nuget.org/v3/index.json" };

        var updates = await CheckForUpdates(packageReferences, packageSourceUris);

        if (updates.Any())
        {
          AnsiConsole.Markup("[bold yellow]Updates found![/]");
          var updateSelection = AnsiConsole.Prompt(
            new MultiSelectionPrompt<PackageUpdate>()
              .Title("Select the packages to update:")
              .PageSize(10)
              .UseConverter(itm => $"{itm.Id} from {itm.CurrentVersion} to {itm.LatestVersion}")
              .MoreChoicesText("[grey](Move up and down to reveal more packages)[/]")
              .InstructionsText("[grey](Press [blue]<space>[/] to toggle a package, [green]<enter>[/] to accept)[/]")
              .AddChoices(updates)
          );

          if (updateSelection.Any())
          {
            await CreatePullRequestWithUpdates(gitClient, repo, updateSelection);
          }
        }
        else
        {
          Console.WriteLine("No updates found.");
        }
      }
    }

    private static List<PackageReference> ParseCsproj(Stream csprojContent)
    {
      XDocument doc = XDocument.Load(csprojContent);
      var packages = doc.XPathSelectElements("//PackageReference");

      var versions = packages
        .Select(node => new { Id = node.Attribute("Include")?.Value, Version = node.Attribute("Version")?.Value })
        .Where(node => node.Id is not null && node.Version is not null)
        .Select(node => new PackageReference(node.Id!, new NuGetVersion(node.Version!)));
      return [.. versions];
    }

    static List<PackageReference> ParseCentralPackageManagement(Stream cpManagementContent)
    {
      XDocument doc = XDocument.Load(cpManagementContent);
      var packages = doc.XPathSelectElements("//ItemGroup/PackageVersion");
      
      var versions = packages
        .Select(node => new { Id = node.Attribute("Include")?.Value, Version = node.Attribute("Version")?.Value })
        .Where(node => node.Id is not null && node.Version is not null)
        .Select(node => new PackageReference(node.Id!, new NuGetVersion(node.Version!)));
      return [.. versions];
    }

    static List<string> GetPackageSources(Stream nugetConfigContent)
    {
      // Implement the logic to read package sources from nuget.config
      return new List<string>();
    }

    static async Task<List<PackageUpdate>> CheckForUpdates(
      List<PackageReference> packageReferences,
      List<string> packageSourceUris
    )
    {
      var updates = new List<PackageUpdate>();
      SourceCacheContext cache = new SourceCacheContext();

      var providers = packageSourceUris.Select(source => Repository.Factory.GetCoreV3(new PackageSource(source)))
        .ToList();
      var sourceCacheContext = new SourceCacheContext();

      foreach (var packageReference in packageReferences)
      {
        foreach (var provider in providers)
        {
          var packageResource = await provider.GetResourceAsync<FindPackageByIdResource>();
          var versions = await packageResource.GetAllVersionsAsync(packageReference.Id, cache, new NullLogger(), CancellationToken.None);
          var latestVersion = versions.Where(v => !v.IsPrerelease).MaxBy(v => v.Version);
          if (latestVersion is not null && latestVersion > packageReference.Version)
          {
            updates.Add(new PackageUpdate(packageReference.Id, packageReference.Version.ToString(), latestVersion.ToString()));
          }
        }
      }

      return updates;
    }

    static async Task CreatePullRequestWithUpdates(GitHttpClient client, GitRepository repo, List<PackageUpdate> updates)
    {
      // get latest commit
      List<GitRef> refs = await client.GetRefsAsync(repo.Id, filter: repo.DefaultBranch).ConfigureAwait(false);
      string latestCommitId = refs.First().ObjectId;
      GitRefUpdate newBranch = new()
      {
        Name = $"refs/heads/nugetupdate-{DateTimeOffset.Now:yyyy-MM-dd}",
        OldObjectId = latestCommitId,
        NewObjectId = latestCommitId
      };
      var branchCreation = await client.UpdateRefsAsync([newBranch], repo.Id);
      
      var change = new GitChange()
      {
        ChangeType = VersionControlChangeType.Edit,
        Item = new GitItem() {Path = }
      }
    }
  }

  public record PackageReference(string Id, NuGet.Versioning.NuGetVersion Version);

  public record PackageUpdate(string Id, string CurrentVersion, string LatestVersion);
  
  public record RepositoryInfo(string Id, string Branch, Repository)
}
