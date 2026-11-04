using TheWitness;

const string SingleInstanceMutexName = "TheWitness-9c6f6a3e-9d2f-4e0e-9b8a-4c3d3d8b6f2c";

using Mutex singleInstanceMutex = new(initiallyOwned: true, name: SingleInstanceMutexName, out bool createdNew);
if (!createdNew)
{
    return;
}

_ = GameFileManifest.Files.Count;

ApplicationConfiguration.Initialize();
Application.Run(new TrayApplicationContext());
