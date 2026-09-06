using System.IO;
using WorkNest.App.Services;
using Xunit;

namespace WorkNest.App.Tests;

/// <summary>Git 仓库定位只检查路径标记，不启动 Git Extensions 或修改用户仓库。</summary>
public sealed class GitIntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("worknest-git-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    [Fact]
    public void FindRepositoryRoot_FromNestedFile_ReturnsGitDirectoryParent()
    {
        var repository = Directory.CreateDirectory(Path.Combine(_root, "repository")).FullName;
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        var nested = Directory.CreateDirectory(Path.Combine(repository, "src", "nested"));
        var file = Path.Combine(nested.FullName, "sample.cs");
        File.WriteAllText(file, "// test");

        Assert.Equal(repository, GitIntegration.FindRepositoryRoot(file));
    }

    [Fact]
    public void FindRepositoryRoot_FromWorktreeMarkerFile_ReturnsWorktreeRoot()
    {
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "worktree")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: C:/repo/.git/worktrees/sample");
        var nested = Directory.CreateDirectory(Path.Combine(worktree, "src"));

        Assert.Equal(worktree, GitIntegration.FindRepositoryRoot(nested.FullName));
    }

    [Fact]
    public void FindRepositoryRoot_OutsideRepository_ReturnsNull()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "plain", "nested"));

        Assert.Null(GitIntegration.FindRepositoryRoot(nested.FullName));
    }
}
