using Xunit;

public sealed class GameCheckTests
{
    [Theory]
    [InlineData("Valorant", "blocked")]
    [InlineData("VALORANT Install", "blocked")]
    [InlineData("Fortnite", "blocked")]
    [InlineData("Call of Duty Warzone", "blocked")]
    [InlineData("GTA V", "partly")]
    [InlineData("Counter-Strike 2", "works")]
    [InlineData("Rust", "blocked")]
    [InlineData("Some indie game", "unknown")]
    public void Known_games_get_an_honest_answer(string name, string status) =>
        Assert.Equal(status, GameCheck.Check(name).Status);

    [Fact]
    public void Words_that_are_also_other_things_do_not_match()
    {
        Assert.Null(GameCheck.Find("rust 1.80 x86_64 pc msvc"));    // the Rust language's installer
        Assert.Null(GameCheck.Find("rustup init"));
        Assert.Null(GameCheck.Find("trust wallet"));
    }

    [Fact]
    public void Answers_point_to_ProtonDB_and_say_how_old_they_are()
    {
        var a = GameCheck.Check("Apex Legends");
        Assert.StartsWith("https://www.protondb.com/search?q=", a.ProtonDb);
        Assert.Contains("late 2025", a.Message);
        Assert.Contains("Restart into Windows", a.Message);
        Assert.Contains("Sober", GameCheck.Check("Roblox").Message);
    }
}
