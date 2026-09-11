using System.Runtime.CompilerServices;

// The stream session's client factory is an internal seam so that tests can drive the session with
// recording clients. Opening real endpoints in a test would take the machine's speakers away from
// whoever runs the suite, and NAudio's own seams cannot be substituted without a device.
[assembly: InternalsVisibleTo("EftToolkit.Tests")]
