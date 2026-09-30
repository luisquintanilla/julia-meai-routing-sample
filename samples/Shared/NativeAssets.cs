using System.Security.Cryptography;

namespace RoutingSamples;

internal static class NativeAssets
{
    internal static void Verify(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Julia asset directory not found: {directory}");
        }

        (string File, string Hash)[] assets =
        [
            ("model.onnx", "97141d0cfb1da6204e9f8f24d581af72eaeb82cda21149d83eaa6df7160fbcd9"),
            ("model.onnx.data", "fd915be810d7ebfb80fb05a48dd33c9484d17ae1b6bcb9e1f544cbaaa913ded1"),
            ("tokenizer.json", "609d8f4c067cd3950f88594c5a802616cea245823836ef5848ee4fc40aab5b6f")
        ];

        foreach (var (file, expected) in assets)
        {
            string path = Path.Combine(directory, file);

            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Missing pinned Julia asset: {file}", path);
            }

            using var stream = File.OpenRead(path);
            string actual = Convert.ToHexStringLower(SHA256.HashData(stream));

            if (actual != expected)
            {
                throw new InvalidDataException($"Julia asset hash mismatch: {file}; refusing unpinned inference.");
            }
        }

        Console.WriteLine("All three Julia asset SHA256 hashes match the pinned export.");
    }
}
