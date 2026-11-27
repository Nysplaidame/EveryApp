namespace Ferry.Engine.Tests;

// Workers = 1 is the production path for hard drives, network and removable drives and rescue copies, and the test rig runs several workers by
// default. These run the core behaviour tests (copy, conflicts, resume, failures, move) again with one worker, so that path stays covered.

/// <summary>Tree copier copy tests, one file at a time.</summary>
public sealed class TreeCopierCopyTests_OneWorker : TreeCopierCopyTests
{
    public TreeCopierCopyTests_OneWorker() => _rig.Workers = 1;
}

/// <summary>Tree copier conflict tests, one file at a time.</summary>
public sealed class TreeCopierConflictTests_OneWorker : TreeCopierConflictTests
{
    public TreeCopierConflictTests_OneWorker() => _rig.Workers = 1;
}

/// <summary>Tree copier resume tests, one file at a time.</summary>
public sealed class TreeCopierResumeTests_OneWorker : TreeCopierResumeTests
{
    public TreeCopierResumeTests_OneWorker() => _rig.Workers = 1;
}

/// <summary>Tree copier failure tests, one file at a time.</summary>
public sealed class TreeCopierFailureTests_OneWorker : TreeCopierFailureTests
{
    public TreeCopierFailureTests_OneWorker() => _rig.Workers = 1;
}

/// <summary>Tree copier move tests, one file at a time.</summary>
public sealed class TreeCopierMoveTests_OneWorker : TreeCopierMoveTests
{
    public TreeCopierMoveTests_OneWorker() => _rig.Workers = 1;
}
