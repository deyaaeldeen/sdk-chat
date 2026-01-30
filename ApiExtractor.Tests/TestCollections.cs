// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Collection for tests that modify global state (environment variables, Console streams).
/// Tests in this collection run sequentially and not in parallel with other tests.
/// </summary>
[CollectionDefinition("ToolPathResolver", DisableParallelization = true)]
public class ToolPathResolverCollection : ICollectionFixture<ToolPathResolverFixture>
{
}

/// <summary>
/// Fixture for ToolPathResolver tests. Can be used to share setup/cleanup logic.
/// </summary>
public class ToolPathResolverFixture
{
    // Empty fixture - used only to control parallelization
}
