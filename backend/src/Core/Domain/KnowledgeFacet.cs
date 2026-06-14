namespace Backend.Core.Domain;

/// <summary>brand_playbook facet — lets each agent pull only its slice (DL-026).</summary>
public enum KnowledgeFacet
{
    Voice,
    Persona,
    Mission,
    VisualStyle,
}
