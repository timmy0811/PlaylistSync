Build
dotnet build

Run the tests
dotnet test
You should see total: 15, failed: 0, succeeded: 15. To run just some of them, filter by name. xunit v3 runs on the new test platform, so the old --filter syntax doesn't apply:
dotnet test -- --filter-class "*ManifestStoreTests"
dotnet test -- --filter-method "*Round_trip*"

Start the app
dotnet run --project src/PlaylistSync.App
For now it's only the empty window, since playlists come in a later milestone. A log file appears in ~/.local/share/playlist-sync/logs/.

Build a standalone release binary
dotnet publish src/PlaylistSync.App -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish/linux
./publish/linux/playlist-sync
Swap in -r win-x64 and -o publish/win for a Windows .exe. It builds on Linux too.