<!--
SPDX-FileCopyrightText: 2026 Joshua Douglas

SPDX-License-Identifier: MIT
-->

# Cesium — Notes for the "export C enums as .NET enums" issue

Personal working notes. **Delete this file before opening the PR** — it should not
end up in the diff. (The SPDX header above is only so REUSE CI doesn't fail if it lingers.)

---

## Contents

1. [The issue, in one sentence](#1-the-issue-in-one-sentence)
2. [Background: what Cesium actually does](#2-background-what-cesium-actually-does)
3. [Repo map](#3-repo-map)
4. [The compilation pipeline](#4-the-compilation-pipeline)
5. [The two core abstractions: IBlockItem and IType](#5-the-two-core-abstractions-iblockitem-and-itype)
6. [How a TypeDefinition actually gets created](#6-how-a-typedefinition-actually-gets-created)
7. [What enums do today, and the exact gap](#7-what-enums-do-today-and-the-exact-gap)
8. [Environment setup](#8-environment-setup)
9. [Git / fork setup](#9-git--fork-setup)
10. [Hands-on: watch it run, start to finish](#10-hands-on-watch-it-run-start-to-finish)
11. [Implementation checklist](#11-implementation-checklist)
12. [Tests to write](#12-tests-to-write)
13. [Contribution mechanics](#13-contribution-mechanics)
14. [Open questions for the maintainer](#14-open-questions-for-the-maintainer)

---

## 1. The issue, in one sentence

Given this C:

```c
typedef enum Side {
    Side_L,
    Side_R
} Side;
```

`Side`, `Side_L` and `Side_R` should be visible from .NET. Today none of them are.

**Scope, per the issue:** export the type *for .NET interop only*. C functions taking
this enum do **not** need to change. Update CLR metadata only — do not use the enum's
values anywhere in Cesium-generated IL. (The maintainer is unsure about function-pointer
compatibility consequences, so that's deliberately out of scope.)

The practical consequence of that constraint: **`EnumType.Resolve` keeps returning `Int32`.**
If any existing `.verified.txt` file changes, I've overreached.

---

## 2. Background: what Cesium actually does

A normal C compiler emits machine code for a physical CPU. Cesium emits code for the
**.NET virtual machine** instead. Four concepts:

- **Assembly** — the output `.dll`/`.exe`. Roughly ".NET's object file + executable in one."
- **IL** (Intermediate Language / CIL / bytecode) — .NET's "machine code". A *stack*
  machine: `ldc.i4.1` pushes the int 1, `stloc.0` pops into local 0, `ret` returns.
- **Metadata** — the part with no C equivalent, and the key to this issue. A .NET assembly
  contains a full structured *description* of every type it defines: names, fields, field
  types, method signatures. Imagine a `.o` file with all its header files embedded in
  machine-readable form. This is why .NET languages interop so easily — a C# project
  references a `.dll` and can see everything inside, no header needed.
- **CLR** — the runtime that loads assemblies and executes IL.

So the project's purpose ("C code is useful but deploying it alongside .NET is painful")
and this issue line up: **a C enum currently produces IL but no metadata, so C# code
referencing a Cesium-compiled library can't see it.**

### Proof, from the existing test suite

Input (`Cesium.CodeGen.Tests/CodeGenEnumTests.cs`):

```c
enum Colour { Red, Green, Blue };
void test() { enum Colour x = Green; }
```

Entire output (`verified/CodeGenEnumTests.EnumDeclarationTest.verified.txt`):

```
System.Void <Module>::test()
  Locals:
    System.Int32 V_0
  IL_0000: ldc.i4.1
  IL_0001: stloc.0
  IL_0002: ret
```

`Colour` gone. `Green` gone — resolved to the literal `1` and inlined. `x` is a plain
`System.Int32`. Nothing survives for C# to reference.

Contrast a struct (`verified/CodeGenTypeTests.NamedStruct.verified.txt`, from
`struct foo { int x[4]; };`):

```
Module: Primary
  Type: <Module>

  Type: foo
  Layout: Sequential
  Fields:
    foo/<SyntheticBuffer>x foo::x
```

A real .NET type named `foo`. **The job is to make enums behave like that.**

---

## 3. Repo map

| Project | Job |
|---|---|
| `Cesium.Preprocessor` | `#include`, `#define` |
| `Cesium.Parser` | text → parse tree |
| `Cesium.Ast` | the parse tree's data classes |
| **`Cesium.CodeGen`** | **the compiler's brain — all my work is here** |
| `Cesium.Compiler` | the `cesium` CLI wrapper |
| `Cesium.Runtime` | C stdlib (`printf` etc.) implemented in C# |
| `*.Tests` | tests, one project per component |
| `Cesium.Sdk`, `Cesium.Templates` | MSBuild integration |

Useful docs already in-repo: `CONTRIBUTING.md`, `docs/tests.md`,
`docs/type-system.md`, `docs/intermediate-representation.md`.

---

## 4. The compilation pipeline

Four stages, for `enum Side { Side_L, Side_R };`:

1. **Parse** — text → a tree mirroring syntax. Produces an `EnumSpecifier`
   (`Cesium.Ast/Declarations.cs`) holding `Side` and the enumerator names. Dumb, faithful.
2. **AST → HIR** (High-level Intermediate Representation) — a cleaned-up form the compiler
   reasons about. `Cesium.CodeGen/Extensions/TranslationUnitEx.cs`. **This is where C's
   implicit enum numbering is applied** (`Side_L`=0, `Side_R`=1).
3. **Lowering** — compiler jargon for *simplifying toward the machine*: rewrite complex
   constructs as simpler ones (a `for` loop becomes a label + conditional jump + goto).
   `Cesium.CodeGen/Ir/Lowering/BlockItemLowering.cs`. Also resolves types and registers names.
4. **Emitting** — walk the lowered tree, write IL and metadata.
   `Cesium.CodeGen/Ir/Emitting/BlockItemEmitting.cs`.

The driver, `Cesium.CodeGen/Contexts/AssemblyContext.cs:63`:

```csharp
var nodes = translationUnit.ToIntermediate(scope);                       // stage 2
nodes = nodes.Select(n => BlockItemLowering.LowerDeclaration(scope, n)).ToList();  // stage 3
foreach (var node in nodes)
    BlockItemEmitting.EmitCode(scope, node);                             // stage 4
```

Note stage 3 is fully materialised (`.ToList()`) **before** stage 4 begins. That ordering
guarantee matters — see §11.

**Mono.Cecil** is the third-party library doing the actual assembly writing. Its classes map
1:1 onto metadata concepts: `ModuleDefinition`, `TypeDefinition`, `FieldDefinition`,
`MethodDefinition`, `Instruction`. A `using Mono.Cecil;` line means that file touches the
output bytes.

### Two context objects thread through everything

- `AssemblyContext` — assembly-wide: the Cecil module, generated-type cache, global fields.
- `TranslationUnitContext` — per-`.c`-file: typedefs (`_types`), tags (`_tags`), functions.

---

## 5. The two core abstractions: IBlockItem and IType

> C# note: an *interface* is a contract — a list of method signatures a class promises to
> provide. Closest C analogy: a struct of function pointers (a vtable) each implementation
> fills in. Calling `someType.Resolve(ctx)` dispatches to whichever implementation that
> object carries. Also: `record` = a class with automatic value equality;
> `internal` = visible only inside this project.

### `IBlockItem` — a piece of program

`Cesium.CodeGen/Ir/BlockItems/IBlockItem.cs` is **empty** — a *marker* interface, just
labelling a class as "a node in the program tree". The folder next to it is recognisably C:
`IfElseStatement.cs`, `WhileStatement.cs`, `ForStatement.cs`, `ReturnStatement.cs`,
`GoToStatement.cs`, `SwitchStatement.cs`…

Because the interface has no methods, behaviour lives in one big `switch` per stage —
`BlockItemLowering.Lower` and `BlockItemEmitting.EmitCode` each have a case per node type.
That's why those files are huge, and why any feature can be traced by grepping its node name
across both.

Two that matter here:

- `EnumConstantDefinition.cs` — the whole file:
  `internal record EnumConstantDefinition(string Identifier, IType Type, IExpression Value) : IBlockItem;`
  One per enumerator.
- `TagBlockItem.cs` — "tag" is the C standard's word for the name in `struct foo` / `enum foo`.
  Means "a type was declared here".

### `IType` — a C type

`Cesium.CodeGen/Ir/Types/IType.cs`. Every C type is an object implementing `IType`:
`PrimitiveType`, `PointerType`, `InPlaceArrayType`, `StructType`, `EnumType`, `FunctionType`,
`ConstType`, `NamedType`. They nest like C types do — `char **argv` becomes
`PointerType(PointerType(PrimitiveType(Char)))`.

The central method:

```csharp
TypeReference Resolve(TranslationUnitContext context);
```

Read it as: **"when this C type reaches .NET, which .NET type does it become?"** That's the
bridge between the two type systems. `docs/type-system.md` is just a human-readable table of
every implementation's answer.

`PrimitiveType.cs` is literally a lookup table:

```csharp
PrimitiveTypeKind.Int  => context.TypeSystem.Int32,
PrimitiveTypeKind.Char => context.TypeSystem.Byte,
PrimitiveTypeKind.Long => context.TypeSystem.Int64,
```

### `IGeneratedType` — types that must write metadata

`IType` answers "what do you *become*". But `struct foo` also needs something to *create* a
.NET type called `foo`. `PrimitiveType` never needs this — `System.Int32` already exists.
So there's a second interface in the same file:

```csharp
internal interface IGeneratedType : IType
{
    TypeDefinition StartEmit(string name, TranslationUnitContext context);
    void FinishEmit(TypeDefinition definition, string name, TranslationUnitContext context);
    bool IsAlreadyEmitted(TranslationUnitContext context);
    void EmitType(TranslationUnitContext context);
}
```

**`StructType` is currently the only implementation.** It is my worked example.
`EnumType` is a plain `IType`. *That is the gap.*

---

## 6. How a TypeDefinition actually gets created

### The reframe: "emit" doesn't write anything

`StartEmit` does **not** write bytes. **Emitting means building an object tree in memory.**
Cecil holds the whole assembly as ordinary C# objects. Exactly one call serialises it, at
`Cesium.Compiler/Compilation.cs:174`:

```csharp
context.VerifyAndGetAssembly().Write(outputFilePath.Value);
```

Everything before that is objects in RAM. So "how does IGeneratedType emit a TypeDefinition?"
→ **it adds an object to a list**, and the list is serialised later.

### The tree

```
AssemblyDefinition                  the .dll
└── ModuleDefinition                its contents
    └── Types                       ← a plain list of TypeDefinition
        ├── TypeDefinition "<Module>"
        │   └── Methods → MethodDefinition
        │                   └── Body.Instructions → Instruction ("ldc.i4.1")
        └── TypeDefinition "foo"
            └── Fields  → FieldDefinition ("x", System.Int32)
```

A `VerifyTypes` test dump is this same tree printed as text. **A type exists in the output
if and only if it is in `Module.Types`.** That is the whole mechanism.

### TypeDefinition vs TypeReference

- **`TypeDefinition`** — a type *this assembly defines*. Ownable, mutable, goes in `Module.Types`.
- **`TypeReference`** — a *handle* to a type, possibly in another assembly. `System.Int32`
  lives in the runtime, so only a reference is possible.

`TypeDefinition` inherits from `TypeReference`. That explains the signatures:
`IType.Resolve` returns `TypeReference` (usually pointing at something that already exists);
`IGeneratedType.StartEmit` returns `TypeDefinition` (it just made one).

**Importing:** to mention a type from another assembly you must record the dependency.
`Module.ImportReference(...)` does that — roughly `#include` + linking. Hence
`StructType.cs:47` importing `System.ValueType`.

### `StructType.StartEmit`, line by line (`StructType.cs:41`)

```csharp
public TypeDefinition StartEmit(string name, TranslationUnitContext context)
{
    var structType = new TypeDefinition(
        context.AssemblyContext.CompilationOptions.Namespace,   // 1. namespace
        Identifier is null ? "<typedef>" + name : Identifier,   // 2. type name
        TypeAttributes.Public | TypeAttributes.Sealed,          // 3. flags
        context.Module.ImportReference(...System.ValueType...)  // 4. base type
    );
    structType.IsSequentialLayout = true;                       // 5. memory layout
    context.Module.Types.Add(structType);                       // 6. ← THE LINE
    return structType;
}
```

1. `""` by default, or `--namespace`. (Interop tests produce `CesiumLib.Greeting`.)
2. From the C tag.
3. Bit flags, `|`-combined exactly like C. `Public` is what makes interop possible
   (see CHANGELOG 0.4.0: "All the Cesium-generated struct types are now `public`").
4. **The base type decides what kind of type this is.** `System.ValueType` → struct.
   **`System.Enum` → enum.** This is the hinge for my change.
5. Fields in declaration order, matching C.
6. Steps 1–5 built an *orphaned* object nothing would see. **This line is what makes it real.**

`FinishEmit` (`StructType.cs:78`) then adds fields:

```csharp
var field = type.CreateFieldOfType(context, definition, identifier!);
definition.Fields.Add(field);
```

with the default in `IType.cs:48`:

```csharp
new FieldDefinition(fieldName, FieldAttributes.Public, ResolveForTypeMember(context))
```

So `struct foo { int x; }` → member is `PrimitiveType(Int)` → `Resolve` → `System.Int32` →
field type. **`IType.Resolve` is the function turning C types into the .NET types used in
metadata**, everywhere.

### Who calls StartEmit? The full chain

Implementing `IGeneratedType` is a contract, not a trigger. Something must *call* it:

```
AssemblyContext.EmitTranslationUnit                             :63
  └─ BlockItemEmitting.EmitCode(scope, node)
       case TagBlockItem t:                     BlockItemEmitting.cs:133
         if (type is StructType g)              ← ENUMS FAIL THIS CHECK
           scope.Context.GenerateType(identifier!, g);
              ▼
       TranslationUnitContext.GenerateType                      :132
         AssemblyContext.GenerateType(...)      ──┐ two phases
         AssemblyContext.GenerateTypeMembers(...)──┤
              ▼                                    │
       AssemblyContext.GenerateType                :384
         type.StartEmit(name, context)   ◄──────────  phase 1: create shell
              ▼                                    ▼
       AssemblyContext.GenerateTypeMembers         :411
         type.FinishEmit(typeDef, name, context) ◄──  phase 2: add members
```

**`if (type is StructType g)` is why enums produce nothing.** An `EnumType` reaches
`EmitCode`, fails the check, falls through silently. I need *both*: make `EnumType` an
`IGeneratedType` **and** widen that check so the methods actually get called.

### Why two phases

```c
struct A { struct B *b; };
struct B { struct A *a; };
```

`A`'s field needs a handle to `B` and vice versa — impossible in one pass. So: create all the
empty shells, then go back and fill in members. Enums have no member types to resolve, so the
split is pure ceremony for me — but the interface requires both.

### The cache (`AssemblyContext.cs:104`)

```csharp
if (!_generatedTypes.ContainsKey(type))
{
    var typeReference = type.StartEmit(name, context);
    _generatedTypes.Add(type, typeReference);
    ...
}
```

Two jobs:

- **Deduplication** — a struct in a header included by two `.c` files gets requested twice.
  Without the cache: duplicate-type error.
- **Lookup** — `StructType.Resolve` (`:126`) is just `context.GetTypeReference(this)`,
  i.e. "give me back the TypeDefinition made for me". It *throws* if not yet generated.

**My asymmetry:** `EnumType.Resolve` will keep returning `Int32` and never consult this cache,
because the issue forbids changing generated IL. So `EnumType` will be an `IGeneratedType`
that generates a type it then declines to use. **Leave a comment citing the issue** — the next
reader will find that surprising.

---

## 7. What enums do today, and the exact gap

`Cesium.CodeGen/Ir/Types/EnumType.cs` in full (the relevant part):

```csharp
public TypeReference Resolve(TranslationUnitContext context)
{
    return context.TypeSystem.Int32;
}
```

That single line is the current state of enum support. The type is *erased* to a plain int.
Correct for generating working IL; exactly why nothing appears in metadata.

### Where the pieces live

| What | Where |
|---|---|
| `EnumSpecifier` → `new EnumType(members, identifier)` | `Ir/Declarations/LocalDeclarationInfo.cs:138-151` |
| Enumerator values computed (implicit `+1`, or `= expr` via `ConstantEvaluator`) | `Extensions/TranslationUnitEx.cs:145-171` (`FindEnumConstants`) |
| Named top-level enum yields `TagBlockItem` **then** the constants | `Extensions/TranslationUnitEx.cs:40-51` |
| Lowering registers each constant as a compile-time name | `Ir/Lowering/BlockItemLowering.cs:405` |
| **Emitting does nothing** — `case EnumConstantDefinition:` `// This is fake declaration` | `Ir/Emitting/BlockItemEmitting.cs:93` |
| **`TagBlockItem` only handles structs** — `if (type is StructType g)` | `Ir/Emitting/BlockItemEmitting.cs:133-143` |

### What a .NET enum looks like in metadata

```
.class public sealed Side extends [System.Runtime]System.Enum
    .field public specialname rtspecialname int32 value__
    .field public static literal valuetype Side Side_L = int32(0)
    .field public static literal valuetype Side Side_R = int32(1)
```

In Cecil:

```csharp
new TypeDefinition(ns, name, TypeAttributes.Public | TypeAttributes.Sealed, importedSystemEnum);

new FieldDefinition("value__",
    FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName,
    context.TypeSystem.Int32);

new FieldDefinition(memberName,
    FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal
        | FieldAttributes.HasDefault,
    enumTypeDef) { Constant = intValue };
```

Import `System.Enum` the same way `StructType.cs:47` imports `System.ValueType`
(via `context.AssemblyContext.MscorlibAssembly`).

### Struct vs enum, side by side

| Struct | Enum |
|---|---|
| `new TypeDefinition(ns, "foo", Public\|Sealed, System.ValueType)` | same, base type `System.Enum` |
| `Module.Types.Add(...)` | identical |
| `FinishEmit` → one field per struct member | `value__` + one `static literal` per enumerator |
| `Resolve` returns the generated type | **`Resolve` keeps returning `Int32`** |

---

## 8. Environment setup

### .NET SDK

`global.json` pins **exactly** `10.0.204` with `rollForward: disable`. A different patch
version is rejected — this is deliberate, so everyone builds identically. Don't use `apt` for
the SDK; it won't have that exact version.

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
/tmp/dotnet-install.sh --version 10.0.204
echo 'export PATH="$HOME/.dotnet:$PATH"' >> ~/.bashrc
export PATH="$HOME/.dotnet:$PATH"
```

### ICU (hit this on Ubuntu 26.04)

`dotnet --version` fails with *"Couldn't find a valid ICU package"*. The names in that error
(`libicu`, `icu-libs`) are for other distros. On Ubuntu it's versioned:

```bash
sudo apt-get install -y libicu78
```

**Do not** use the suggested `System.Globalization.Invariant=true` escape hatch — this repo
has culture-sensitive tests (`Cesium.CodeGen.Tests/UseInvariantCultureAttribute.cs` exists for
a reason) and it'd cause phantom failures.

Note: miniconda has ICU 73 in `~/miniconda3/lib`. Don't point .NET at it — version mismatch,
fragile `LD_LIBRARY_PATH` hack. Install the system package.

### Build and test

```bash
cd ~/open-source/Cesium
dotnet build
dotnet test Cesium.CodeGen.Tests     # fast inner loop
```

`dotnet nuke TestAll` (per CONTRIBUTING.md) runs everything including integration tests —
much slower, save it for pre-PR. Needs `dotnet tool restore` first.

---

## 9. Git / fork setup

Already done. Current state:

```
origin    git@github.com:Dread63/Cesium.git      ← my fork, push here
upstream  git@github.com:ForNeVeR/Cesium.git     ← the real project, read-only
```

Working branch: `enum-metadata`.

I can't push to `ForNeVeR/Cesium`, so the model is: push to my fork, then open a PR across.

```bash
git push -u origin enum-metadata     # origin = my fork, NOT upstream
```

### SSH gotcha

`~/.ssh/config` defines aliases `github-personal` and `github-work` — **neither is
`github.com`**. So a plain `git@github.com:…` URL matches no block and falls back to whatever
the agent offers. It happens to land on the personal key, but if the work key were offered
first I'd authenticate as the wrong account. To pin it:

```bash
git remote set-url origin   git@github-personal:Dread63/Cesium.git
git remote set-url upstream git@github-personal:ForNeVeR/Cesium.git
```

A `Permission denied (publickey)` usually means the agent doesn't have the key loaded yet in
that shell. Check with `ssh-add -l`, verify with `ssh -T git@github.com` (should print
*"Hi Dread63!"*).

### Syncing with upstream later

```bash
git fetch upstream
git switch main && git merge --ff-only upstream/main
git switch enum-metadata && git rebase main
```

---

## 10. Hands-on: watch it run, start to finish

Six exercises, each standalone. Steps 1–3 are the essentials; 4–5 are where the mental model
locks in.

### Step 1 — Green baseline

```bash
dotnet build
dotnet test Cesium.CodeGen.Tests
```

Everything passing matters more than it sounds: from here, *any* failure is something I caused.

### Step 2 — See the front of the pipeline, no code changes

```bash
cat > /tmp/side.c <<'EOF'
typedef enum Side {
    Side_L,
    Side_R
} Side;

int main(void) { return Side_R; }
EOF

dotnet run --project Cesium.Compiler -- -E /tmp/side.c          # preprocessor output
dotnet run --project Cesium.Compiler -- --ast-dump /tmp/side.c  # parse tree
dotnet run --project Cesium.Compiler -- -o /tmp/side.dll /tmp/side.c   # real compile
```

Expect the AST dump to contain an `EnumSpecifier` with `Side`, `Side_L`, `Side_R` — the
parser's faithful transcription, before numbering or type resolution.

### Step 3 — See the bug with my own eyes

Every test in `CodeGenEnumTests.cs` uses a `DoTest` helper that dumps **methods** (IL).
Add one that dumps **types** (metadata):

```csharp
[MustUseReturnValue]
private static Task DoTypeTest(string source)
{
    var assembly = GenerateAssembly(default, source);
    return VerifyTypes(assembly);
}

[Fact]
public Task EnumMetadataDump() => DoTypeTest(@"
typedef enum Side { Side_L, Side_R } Side;
");

[Fact]
public Task StructMetadataDump() => DoTypeTest(@"
struct Point { int x; int y; };
");
```

```bash
dotnet test Cesium.CodeGen.Tests --filter "FullyQualifiedName~MetadataDump"
```

**They fail** — no recorded expected-output file exists yet. That's the point. The failure
writes what was actually produced to `verified/CodeGenEnumTests.EnumMetadataDump.received.txt`:

```bash
cat Cesium.CodeGen.Tests/verified/CodeGenEnumTests.*.received.txt
```

- Enum version: a `Module:` line and `Type: <Module>`, and **no `Type: Side`**. That empty
  space is the issue.
- Struct version: `Type: Point`, `Layout: Sequential`, both fields.

Same compiler, same harness, one produces metadata and the other doesn't.

**This is the workflow for the real fix:** change code → run test → read `.received.txt` →
when the output is right, `pwsh -c ./scripts/approve-all.ps1` promotes every `.received.txt`
to `.verified.txt`. (`*.received.txt` is gitignored, so strays won't get committed.)

Clean up: `rm Cesium.CodeGen.Tests/verified/*.received.txt`

### Step 4 — Trace the pipeline live

Temporarily replace the body of `EmitTranslationUnit` (`AssemblyContext.cs:63`):

```csharp
public void EmitTranslationUnit(string name, TranslationUnit translationUnit)
{
    var context = new TranslationUnitContext(this, name);
    var scope = context.GetInitializerScope();

    var nodes = translationUnit.ToIntermediate(scope).ToList();
    foreach (var n in nodes) Console.WriteLine($"[HIR]   {n.GetType().Name}");

    var lowered = nodes.Select(node => BlockItemLowering.LowerDeclaration(scope, node)).ToList();
    foreach (var n in lowered) Console.WriteLine($"[LOWER] {n.GetType().Name}");

    foreach (var node in lowered)
    {
        Console.WriteLine($"[EMIT]  {node.GetType().Name}");
        BlockItemEmitting.EmitCode(scope, node);
    }
}
```

> C# gotcha: `ToIntermediate` returns a **lazy** sequence — it computes nothing until
> iterated, and re-runs if iterated twice. Hence `.ToList()`. Without it, printing would
> silently run stage 2 a second time. This `IEnumerable` + `yield return` pattern is
> everywhere in this codebase.

```bash
dotnet run --project Cesium.Compiler -- -o /tmp/side.dll /tmp/side.c
```

Expect roughly:

```
[HIR]   TypeDefBlockItem
[HIR]   EnumConstantDefinition
[HIR]   EnumConstantDefinition
[HIR]   FunctionDefinition
[LOWER] ...
[EMIT]  TypeDefBlockItem
[EMIT]  EnumConstantDefinition
```

Those are the classes from `Ir/BlockItems/` flowing through the stages. Then edit `/tmp/side.c`
— add an `if`, a `for`, a struct — and re-run. A `for` appears in `[HIR]` as `ForStatement` and
has *vanished* by `[LOWER]`, replaced by labels and gotos. That's lowering, live.

### Step 5 — Catch a type being created

One line in `StructType.cs`, just before `context.Module.Types.Add(structType);` (line 74):

```csharp
Console.WriteLine($">>> StartEmit creating .NET type: {structType.FullName}");
```

```bash
printf 'struct Point { int x; int y; };\nint main(void){return 0;}\n' > /tmp/point.c
dotnet run --project Cesium.Compiler -- -o /tmp/point.dll /tmp/point.c   # prints >>> Point
dotnet run --project Cesium.Compiler -- -o /tmp/side.dll  /tmp/side.c    # prints NOTHING
```

The enum's `TypeDefBlockItem` *was* processed (step 4 proves it) — it just hit
`if (type is StructType g)`, failed, and fell through. **That's the bug, observed end to end.**

Revert: `git diff` to review, then `git checkout -- Cesium.CodeGen/`, and delete the temp tests.

### Step 6 — A real debugger (optional)

VS Code + WSL extension + **C# Dev Kit**. Breakpoint on
`context.Module.Types.Add(structType)`, then "Debug Test" on `CodeGenTypeTests.NamedStruct`.
Most valuable thing there: read the **call stack** panel — it shows the chain from §6 as
something clickable.

---

## 11. Implementation checklist

1. **`Ir/Types/EnumType.cs`** — implement `IGeneratedType`.
   - `StartEmit`: shell (base `System.Enum`) + the `value__` field; `Module.Types.Add`.
   - `FinishEmit`: one `static literal` field per enumerator.
   - `IsAlreadyEmitted` / `EmitType`: copy `StructType.cs:113-118`.
   - **Leave `Resolve` returning `Int32`**, with a comment citing the issue.

2. **Getting the values inside `FinishEmit`.** `FindEnumConstants` needs an
   `IDeclarationScope`, which `FinishEmit` doesn't receive. Use
   `context.GetInitializerScope()` — the same `GlobalConstructorScope` the translation unit
   was lowered against. Because lowering fully completes before emitting (§4), every
   enumerator is already registered as a variable, so `Blue = Green + 10` resolves
   (`ConstantEvaluator.cs:88-94`).

   *Rejected alternative:* adding the field from the `EnumConstantDefinition` emit case.
   Looks smaller, but breaks when the same enum appears in two translation units (duplicate
   fields). Routing through `FinishEmit` inherits the `_generatedFieldsTypes` dedup for free.
   `CodeGenMethodTests.EnumParametersFromDifferentModules` is exactly that case — make sure
   it still passes.

   *Watch:* `FindEnumConstants` tracks `long currentValue`, but `Constant` must be an `int`
   to match `value__`. Narrowing needed.

3. **`AssemblyContext.cs:376-433`** — `GenerateTypeName`, `GenerateType`,
   `GenerateTypeMembers` are typed to `StructType`. Widen to `IGeneratedType`, keeping the
   member-recursion block guarded by `if (type is StructType st)`. Same for
   `TranslationUnitContext.GenerateType` (`:132`).

4. **`BlockItemEmitting.cs:133-152`** — change both `type is StructType g` to
   `type is IGeneratedType g`, in the `TagBlockItem` **and** `TypeDefBlockItem` cases
   (the latter covers `typedef enum … X;`).

5. **Anonymous enums** (`enum { Red, Green };`) must stay unemitted — no tag, nothing to
   export. This already falls out: `TranslationUnitEx.cs:42` only yields a `TagBlockItem`
   when `identifier is not null`. Just don't break it.

---

## 12. Tests to write

- **Characterization test** in `CodeGenEnumTests.cs` using a `VerifyTypes`-based helper
  (see §10 step 3 — the existing `DoTest` dumps IL only and won't show the new type).
  Cover: named tag, `typedef enum`, explicit values (`= 100`), and an anonymous enum
  asserting no type appears.
- **Regenerate gold files**: `pwsh -c ./scripts/approve-all.ps1`. **Read the diff before
  committing** — if any *existing* `.verified.txt` changed, I broke the "IL is untouched"
  invariant from §1.
- **End-to-end interop test** — the one that actually proves the issue is fixed.
  `CodeGenNetInteropTests.DoTestCLibCSharpApp` compiles C, then compiles a C# program against
  it and runs it. The C# would be `if (CesiumLib.Side.Side_L != 0) return 1;` — which won't
  compile unless the metadata is a genuinely usable enum.
- `Cesium.IntegrationTests/enum.c` already covers C-side semantics; it should keep passing
  untouched.

---

## 13. Contribution mechanics

- **License header** on every new file:
  ```
  // SPDX-FileCopyrightText: 2026 Cesium contributors <https://github.com/ForNeVeR/Cesium>
  //
  // SPDX-License-Identifier: MIT
  ```
  (Attributing to "Cesium contributors" is explicitly fine per CONTRIBUTING.md.)
- **Encoding**: `pwsh -File scripts/Test-Encoding.ps1 -AutoFix` if CI complains about line
  endings / BOM.
- **CHANGELOG.md**: add an entry under a new `## [Unreleased]` → `### Added`, matching the
  existing style (issue link + "Thanks to @Dread63!").
- **`docs/type-system.md`**: consider adding a row — enums are conspicuously absent from that
  table.
- **Delete this notes file** before opening the PR.

---

## 14. Open questions for the maintainer

Worth asking on the issue *before* writing much code:

1. For `typedef enum { Red, Green } Colour;` (anonymous tag, named typedef), `StructType`
   names the equivalent case `<typedef>Colour` (`StructType.cs:46`). Should enums follow that
   convention, or use the bare typedef name `Colour`? The latter seems friendlier for the
   .NET interop goal, but I don't want to break a convention.
2. Confirm anonymous enums (`enum { Red, Green };`) should stay unexported — no tag to name
   the type. (Fairly confident the answer is yes; cheap to confirm.)

### Draft issue comment

> I'd like to take this one, if it's still open — it'd be my first contribution here.
>
> From reading the code, my plan is to make `EnumType` implement `IGeneratedType` (mirroring
> `StructType`) so a named enum emits a `TypeDefinition` deriving from `System.Enum`, with a
> `literal` field per enumerator. Per the issue scope, `EnumType.Resolve` would keep returning
> `Int32`, so no generated IL changes — metadata only.
>
> One question before I start: for `typedef enum { Red, Green } Colour;`, `StructType` names
> the anonymous-tag case `<typedef>Colour`. Should enums follow that convention, or use the
> bare typedef name `Colour`? The latter seems friendlier for the .NET interop goal here, but
> I don't want to break the naming convention.
>
> Also — should anonymous enums (`enum { Red, Green };`) stay unexported, since there's no tag
> to name the type?

Don't promise a timeline. Don't block on the reply to start — the environment setup and
§10 exercises don't depend on the answer.

---

## Quick reference

| Command | What |
|---|---|
| `dotnet build` | build |
| `dotnet test Cesium.CodeGen.Tests` | fast inner loop |
| `dotnet test Cesium.CodeGen.Tests --filter "FullyQualifiedName~Enum"` | one group |
| `dotnet nuke TestAll` | everything (slow, pre-PR) |
| `dotnet run --project Cesium.Compiler -- -o out.dll in.c` | compile a C file |
| `dotnet run --project Cesium.Compiler -- --ast-dump in.c` | dump the parse tree |
| `pwsh -c ./scripts/approve-all.ps1` | accept new test output as expected |
| `git push -u origin enum-metadata` | push to my fork |

| File | Why it matters |
|---|---|
| `Cesium.CodeGen/Ir/Types/IType.cs` | `IType` + `IGeneratedType` definitions |
| `Cesium.CodeGen/Ir/Types/StructType.cs` | **the template to copy** |
| `Cesium.CodeGen/Ir/Types/EnumType.cs` | **the file to change** |
| `Cesium.CodeGen/Ir/Emitting/BlockItemEmitting.cs:133` | the `is StructType` check to widen |
| `Cesium.CodeGen/Contexts/AssemblyContext.cs:384,411` | `GenerateType` / `GenerateTypeMembers` |
| `Cesium.CodeGen/Extensions/TranslationUnitEx.cs:145` | `FindEnumConstants` — where values come from |
| `Cesium.CodeGen.Tests/CodeGenEnumTests.cs` | where my tests go |
