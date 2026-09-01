// SPDX-FileCopyrightText: 2025 Cesium contributors <https://github.com/ForNeVeR/Cesium>
//
// SPDX-License-Identifier: MIT

using Cesium.CodeGen.Contexts;
using Cesium.CodeGen.Ir.Declarations;
using Mono.Cecil;

namespace Cesium.CodeGen.Ir.Types;

internal sealed class EnumType : IGeneratedType, IEquatable<EnumType>, IEquatable<IGeneratedType>
{
    public EnumType(IReadOnlyList<InitializableDeclarationInfo> members, string? identifier)
    {
        Members = members;
        Identifier = identifier;
    }

    /// <inheritdoc />
    public TypeKind TypeKind => TypeKind.Enum;

    internal IReadOnlyList<InitializableDeclarationInfo> Members { get; }
    public string? Identifier { get; }

    public TypeReference Resolve(TranslationUnitContext context)
    {
        return context.TypeSystem.Int32;
    }

    public int? GetSizeInBytes(TargetArchitectureSet arch)
    {
        return 4;
    }

    public TypeDefinition StartEmit(string name, TranslationUnitContext context)
    {
        var enumType = new TypeDefinition(
            context.AssemblyContext.CompilationOptions.Namespace,
            Identifier is null ? "<typedef>" + name : Identifier,
            TypeAttributes.Public | TypeAttributes.Sealed,
            context.Module.ImportReference(new TypeReference("System", "Enum", context.AssemblyContext.MscorlibAssembly.MainModule, context.AssemblyContext.MscorlibAssembly.MainModule.TypeSystem.CoreLibrary)));

        context.Module.Types.Add(enumType);
        return enumType;
    }

    public bool IsAlreadyEmitted(TranslationUnitContext context) => context.GetTypeReference(this) is not null;

    public void EmitType(TranslationUnitContext context) {

        var name = this.Identifier ?? "anonymous_enum";
        context.GenerateType(name, this);
    }

    public void FinishEmit(TypeDefinition definition, string name, TranslationUnitContext context)
    {

    }

    public bool Equals(EnumType? other)
    {
        if (other is null) return false;

        if (Identifier != other.Identifier) return false;

        if (Members.Count != other.Members.Count) return false;
        for (var i = 0; i < Members.Count; i++)
        {
            if (!Members[i].Equals(other.Members[i])) return false;
        }

        return true;
    }

    public bool Equals(IGeneratedType? other)
    {
        if (other is EnumType enumType)
        {
            return Equals(enumType);
        }

        return false;
    }

    public override bool Equals(object? other)
    {
        if (other is EnumType)
        {
            return Equals((EnumType)other);
        }

        return false;
    }

    public override int GetHashCode()
    {
        var hash = (Identifier?.GetHashCode() ?? 0) ^ 0;
        foreach (var m in Members)
        {
            hash ^= m.GetHashCode();
        }

        return hash;
    }
}
