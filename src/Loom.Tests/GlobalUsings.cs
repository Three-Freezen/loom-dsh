// Loom.Tests uses the WPF SDK (UseWPF=true) so the implicit-usings set no longer includes System.IO;
// most tests use Path/File/Directory heavily. Keep the previous behavior explicit.
global using System.IO;
