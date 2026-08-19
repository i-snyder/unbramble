using UnBramble.Core.Scanning;

namespace UnBramble.Tests;

public class HiddenAssetRulesTests
{
    [Theory]
    [InlineData(".git", true, true)]                  // dot-prefixed directory
    [InlineData("Samples~", true, true)]               // tilde-suffixed folder
    [InlineData("notatilde~file", false, false)]       // file ending in ~ is NOT excluded (folders only)
    [InlineData("CVS", true, true)]                    // "cvs" name, case-insensitive, as a directory
    [InlineData("cvs", false, true)]                   // "cvs" name, case-insensitive, as a file
    [InlineData("foo.tmp", false, true)]                // file ending in .tmp
    [InlineData("foo.tmp.meta", false, true)]           // excluded because its owner ("foo.tmp") is
    [InlineData("Foo.cs", false, false)]                // normal file name
    [InlineData("Assets", true, false)]                 // normal directory name
    [InlineData("Foo.cs.meta", false, false)]           // normal meta, owner not hidden
    [InlineData(".hidden.mat", false, true)]            // dot-prefixed file
    public void IsHidden_MatchesDocumentedRules(string name, bool isDirectory, bool expectedHidden)
    {
        Assert.Equal(expectedHidden, HiddenAssetRules.IsHidden(name, isDirectory));
    }
}
