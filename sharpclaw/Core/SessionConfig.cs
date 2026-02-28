using System;
using System.Collections.Generic;
using System.Text;

namespace sharpclaw.Core;

public class SessionConfig
{
    public required string SessionId { get; set; }

    public required string SessionName { get; set; }

    public string? WorkspacePath { get; set; }
}
