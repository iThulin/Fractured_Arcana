# Negotiation portraits — art contract

`NegotiationPortrait.cs` resolves textures from this folder by naming convention.
Drop PNGs in; no code changes needed — the widget lights up per file it finds.

## Naming

```
{archetype}_{stance}.png     the expression portraits (the stance IS game info)
{archetype}_base.png         neutral fallback used when a stance PNG is missing
```

- `archetype` ∈ `merchant`, `commander`, `scholar`, `opportunist`, `idealist`, `survivor`
- `stance` ∈ `eager`, `guarded`, `wavering`, `irritated`, `expansive`

Full coverage = 30 stance portraits + 6 bases. Until a file exists, the widget
shows a styled placeholder (archetype initial + stance word), so ship order is
free — start with one archetype's 5 stances to evaluate the feel.

## Specs

- Square, ~512×512. Displayed in a ~150px circular frame (ring tint = tension
  zone, drawn by the widget — don't bake a border into the art).
- Waist-up, painterly per `painterly_style_guide.md` §1 (muted range + one warm
  accent), readable at 150px: expression changes must be legible at a glance,
  since stances modify how every token lands (Module A).
- Suggested expression anchors, per `negotiation_redesign_v1.md` §5.2:
  - eager — leaning in, appetite plain
  - guarded — arms crossed, closed posture
  - wavering — glancing down at the contract, uncertain
  - irritated — jaw set, narrowed eyes
  - expansive — open hands, easy warmth (Cordial-only stance)
