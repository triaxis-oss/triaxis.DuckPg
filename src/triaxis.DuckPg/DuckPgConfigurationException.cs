namespace triaxis.DuckPg;

/// A lake that cannot be built from what it was given, said in a sentence a command line can print.
/// Thrown before anything is opened, so the path that is wrong is still the subject.
public sealed class DuckPgConfigurationException(string message) : Exception(message);
