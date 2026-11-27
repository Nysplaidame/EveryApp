using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("EveryApp.Core.Tests")]
[assembly: InternalsVisibleTo("Ferry.Engine.Tests")] // the journal's failure seam (JournalIo), to test how a job handles a journal that cannot be written
