# Why questions and answers are polymorphic records, not C# `union`

Tested 2026-10-11 with SDK 11.0.100-rc.1.26425.128, `LangVersion=preview`, console app (probe source: `union-probe.cs.txt`).

| Check | Result |
|---|---|
| `public union Question(Noul, Choice);` compiles | Yes. Lowered to a **struct** (`System.ValueType`) implementing `IUnion` with a single `object Value` property. Pattern matching (`q switch { Noul n => ... }`) works. |
| `[JsonPolymorphic]` / `[JsonDerivedType]` on the union | **No.** `error CS0592: Attribute 'JsonPolymorphic' is not valid on this declaration type. It is only valid on 'class, interface' declarations.` |
| `JsonSerializer.Serialize(union)` | Serializes the case value only: `{"Instructions":"x"}`, **no `type` discriminator**. |
| `JsonSerializer.Deserialize<Question>(...)` with `"type":"noul"` | **Throws** `JsonException: JSON value type 'Object' is ambiguous for union type 'Question' because multiple case types can use this value type`. STJ's union support picks a case by JSON token kind, not by discriminator. |

Conclusion: unions compile, but STJ can't do a `type`-discriminated union with them without a hand-written converter, and they need a preview language version. Decision: `abstract record JevQuestion` + `[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]` + `[JsonDerivedType]` for `NoulQuestion` / `ChoiceQuestion` / `ScoreQuestion`. Records still pattern-match exhaustively enough for callers. Worth looking at again when .NET 11 goes GA.

`JevState` (string | object | text array) is a sealed class with `Kind`, factory methods (`FromText`, `FromTexts`, `FromJson`, `FromObject<T>`), implicit conversions from `string`/`string[]`, and a custom `JsonConverter` that picks the shape by token kind (exactly the case STJ's union support handles, but a class is stable and doesn't need preview).
