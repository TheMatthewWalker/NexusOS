using Xunit;

public sealed class WindowsProgramTests
{
    [Theory]
    [InlineData("SpotifySetup.exe", "Spotify")]
    [InlineData("ChromeSetup.exe", "Chrome")]
    [InlineData("DiscordSetup-1.0.9.x64.exe", "Discord")]
    [InlineData("vlc-3.0.21-win64.exe", "vlc")]
    [InlineData("/home/ada/Downloads/ZoomInstallerFull.msi", "Zoom")]
    [InlineData("npp.8.6.9.Installer.x64.exe", "npp")]
    [InlineData("OBS-Studio-30.2-Full-Installer-x64.exe", "OBS Studio")]
    public void An_installer_file_name_becomes_the_program(string file, string program) =>
        Assert.Equal(program, MoveInApps.ProgramFromFileName(file));

    [Theory]
    [InlineData("SpotifySetup.exe", "Spotify", true)]
    [InlineData("ChromeSetup.exe", "Google Chrome", true)]
    [InlineData("vlc-3.0.21-win64.exe", "VLC", false)]
    [InlineData("Notepad++ Installer.exe", "Text Editor", false)]
    [InlineData("Photoshop_Set-Up.exe", "GIMP", false)]
    public void It_is_matched_to_an_app_here(string file, string name, bool same)
    {
        var match = MoveInApps.Match([MoveInApps.ProgramFromFileName(file)]).Single();
        Assert.Equal(name, match.Name);
        Assert.Equal(same, match.Same);
    }

    [Fact]
    public void Unknown_programs_have_no_match() =>
        Assert.Empty(MoveInApps.Match([MoveInApps.ProgramFromFileName("Q7xWidget.exe")]));
}
