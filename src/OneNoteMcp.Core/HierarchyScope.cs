namespace OneNoteMcp.Core;

/// <summary>Core's own hierarchy scope. Com maps it to the interop enum; no COM type leaks into Core (AD-2).</summary>
public enum HierarchyScope
{
    Self,
    Children,
    Notebooks,
    Sections,
    Pages,
}
