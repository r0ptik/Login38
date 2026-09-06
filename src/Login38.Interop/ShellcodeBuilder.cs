using System.Buffers.Binary;

namespace Login38.Interop;

/// <summary>
/// Builds a block of x86 machine code destined for a known address in the game process.
/// </summary>
/// <remarks>
/// <para>
/// The reference implementation assembled shellcode by pushing raw bytes into a vector
/// and computing relative displacements by hand, for example:
/// </para>
/// <code>
/// sc.push(0xE9);
/// let jmp_from = sc_addr + sc.len() as u32 + 4;
/// let rel = target.wrapping_sub(jmp_from) as i32;
/// </code>
/// <para>
/// That arithmetic — "the displacement is measured from the end of the instruction, and
/// the end is four bytes after the opcode I just pushed" — is the single most
/// error-prone part of the whole patching layer, and getting it wrong writes a jump
/// into arbitrary memory. This type owns that calculation so no call site repeats it.
/// </para>
/// <para>
/// The builder must know where the code will finally live, because relative jumps
/// depend on it. Allocate first, then build.
/// </para>
/// </remarks>
public sealed class ShellcodeBuilder
{
    private const byte JmpRel32 = 0xE9;
    private const byte CallRel32 = 0xE8;
    private const int Rel32Size = 4;

    private readonly List<byte> _code = [];

    /// <param name="baseAddress">Where the finished block will be written.</param>
    public ShellcodeBuilder(GameAddress baseAddress) => BaseAddress = baseAddress;

    /// <summary>Where the finished block will be written.</summary>
    public GameAddress BaseAddress { get; }

    /// <summary>Bytes emitted so far.</summary>
    public int Length => _code.Count;

    /// <summary>Address the next emitted byte will occupy.</summary>
    public GameAddress CurrentAddress => BaseAddress + Length;

    /// <summary>A forward branch whose displacement is not known yet.</summary>
    public readonly record struct Label(int DisplacementIndex);

    public ShellcodeBuilder Byte(byte value)
    {
        _code.Add(value);
        return this;
    }

    /// <summary>Emits raw opcode bytes.</summary>
    public ShellcodeBuilder Bytes(params ReadOnlySpan<byte> values)
    {
        _code.AddRange(values);
        return this;
    }

    public ShellcodeBuilder Dword(uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return Bytes(buffer);
    }

    public ShellcodeBuilder Word(ushort value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        return Bytes(buffer);
    }

    /// <summary><c>pushfd</c> — saves the flags.</summary>
    /// <remarks>
    /// Needed by any detour that lands between a comparison and the branch that reads it,
    /// which is where the client's own code has the values worth looking at.
    /// </remarks>
    public ShellcodeBuilder PushFd() => Byte(0x9C);

    /// <summary><c>popfd</c>.</summary>
    public ShellcodeBuilder PopFd() => Byte(0x9D);

    /// <summary><c>pushad</c> — saves every general-purpose register.</summary>
    public ShellcodeBuilder PushAd() => Byte(0x60);

    /// <summary><c>popad</c> — restores what <see cref="PushAd"/> saved.</summary>
    public ShellcodeBuilder PopAd() => Byte(0x61);

    /// <summary><c>mov eax, imm32</c>.</summary>
    public ShellcodeBuilder MovEax(uint value) => Byte(0xB8).Dword(value);

    /// <summary><c>mov ecx, imm32</c>.</summary>
    public ShellcodeBuilder MovEcx(uint value) => Byte(0xB9).Dword(value);

    /// <summary><c>mov edx, imm32</c>.</summary>
    public ShellcodeBuilder MovEdx(uint value) => Byte(0xBA).Dword(value);

    /// <summary><c>mov edi, imm32</c>.</summary>
    public ShellcodeBuilder MovEdi(uint value) => Byte(0xBF).Dword(value);

    /// <summary><c>mov dword ptr [address], imm32</c>.</summary>
    public ShellcodeBuilder MovDwordPtr(GameAddress address, uint value) =>
        Bytes([0xC7, 0x05]).Dword(address.Value).Dword(value);

    /// <summary>
    /// <c>mov dword ptr [ebp+displacement], imm32</c> — writes a local of the frame the
    /// hook was entered from.
    /// </summary>
    /// <remarks>
    /// Only valid inside a cave entered by a jump rather than a call, where <c>ebp</c> is
    /// still the hooked function's frame pointer.
    /// </remarks>
    public ShellcodeBuilder MovLocalDword(int displacement, uint value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(displacement, sbyte.MinValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(displacement, sbyte.MaxValue);

        return Bytes([0xC7, 0x45, unchecked((byte)(sbyte)displacement)]).Dword(value);
    }

    /// <summary><c>mov dword ptr [ebp+displacement], eax</c>.</summary>
    /// <inheritdoc cref="MovLocalDword" path="/remarks"/>
    public ShellcodeBuilder MovLocalFromEax(int displacement) => LocalOperand([0x89, 0x45], [0x89, 0x85], displacement);

    /// <summary><c>mov edx, dword ptr [ebp+displacement]</c>.</summary>
    /// <inheritdoc cref="MovLocalDword" path="/remarks"/>
    public ShellcodeBuilder MovEdxLocal(int displacement) => LocalOperand([0x8B, 0x55], [0x8B, 0x95], displacement);

    /// <summary><c>mov dword ptr [edx+displacement], eax</c>.</summary>
    public ShellcodeBuilder MovEdxPtrFromEax(byte displacement) => Bytes([0x89, 0x42, displacement]);

    /// <summary>
    /// Emits an <c>[ebp+disp]</c> operand in whichever form the displacement fits.
    /// </summary>
    /// <remarks>
    /// A frame this far from <c>ebp</c> needs the four-byte form, and the two forms differ
    /// in the ModR/M byte rather than only in the displacement — which is why the caller
    /// passes both encodings rather than one plus a size.
    /// </remarks>
    private ShellcodeBuilder LocalOperand(
        ReadOnlySpan<byte> shortForm, ReadOnlySpan<byte> longForm, int displacement)
    {
        if (displacement is >= sbyte.MinValue and <= sbyte.MaxValue)
        {
            return Bytes(shortForm).Byte(unchecked((byte)(sbyte)displacement));
        }

        return Bytes(longForm).Dword(unchecked((uint)displacement));
    }

    /// <summary><c>push dword ptr [ebp+displacement]</c>.</summary>
    /// <inheritdoc cref="MovLocalDword" path="/remarks"/>
    public ShellcodeBuilder PushLocal(int displacement)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(displacement, sbyte.MinValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(displacement, sbyte.MaxValue);

        return Bytes([0xFF, 0x75, unchecked((byte)(sbyte)displacement)]);
    }

    /// <summary><c>mov al, byte ptr [address]</c>.</summary>
    public ShellcodeBuilder MovAlFrom(GameAddress address) => Byte(0xA0).Dword(address.Value);

    /// <summary><c>mov eax, dword ptr [address]</c>.</summary>
    public ShellcodeBuilder MovEaxFrom(GameAddress address) => Byte(0xA1).Dword(address.Value);

    /// <summary><c>mov dword ptr [address], eax</c>.</summary>
    public ShellcodeBuilder MovEaxTo(GameAddress address) => Byte(0xA3).Dword(address.Value);

    /// <summary><c>mov byte ptr [address], imm8</c>.</summary>
    public ShellcodeBuilder MovBytePtr(GameAddress address, byte value) =>
        Bytes([0xC6, 0x05]).Dword(address.Value).Byte(value);

    /// <summary><c>movsx edx, byte ptr [address]</c> — a signed byte widened to a dword.</summary>
    public ShellcodeBuilder MovsxEdxByteFrom(GameAddress address) =>
        Bytes([0x0F, 0xBE, 0x15]).Dword(address.Value);

    /// <summary><c>movsx edx, word ptr [address]</c> — a signed word widened to a dword.</summary>
    public ShellcodeBuilder MovsxEdxWordFrom(GameAddress address) =>
        Bytes([0x0F, 0xBF, 0x15]).Dword(address.Value);

    /// <summary><c>add eax, edx</c>.</summary>
    public ShellcodeBuilder AddEaxEdx() => Bytes([0x03, 0xC2]);

    /// <summary><c>push eax</c>.</summary>
    public ShellcodeBuilder PushEax() => Byte(0x50);

    /// <summary><c>pop eax</c>.</summary>
    public ShellcodeBuilder PopEax() => Byte(0x58);

    /// <summary><c>push esp</c> — the address of whatever was pushed last.</summary>
    /// <remarks>
    /// From the 386 onwards this pushes the value <c>esp</c> held before the instruction
    /// ran, so a <c>push</c> of a placeholder followed by this one leaves the placeholder's
    /// own address on the stack. That is how a detour hands an API an out parameter without
    /// allocating anywhere to put it — and, unlike a fixed slot, without two threads inside
    /// the same detour overwriting each other's answer.
    /// </remarks>
    public ShellcodeBuilder PushEsp() => Byte(0x54);

    /// <summary><c>pop esi</c>.</summary>
    public ShellcodeBuilder PopEsi() => Byte(0x5E);

    /// <summary><c>mov ecx, dword ptr [address]</c> — the <c>this</c> of a thiscall.</summary>
    public ShellcodeBuilder MovEcxFrom(GameAddress address) => Bytes([0x8B, 0x0D]).Dword(address.Value);

    /// <summary><c>push ecx</c>.</summary>
    public ShellcodeBuilder PushEcx() => Byte(0x51);

    /// <summary><c>pop ecx</c>.</summary>
    public ShellcodeBuilder PopEcx() => Byte(0x59);

    /// <summary><c>mov ecx, dword ptr [eax]</c> — the first field of whatever eax points at.</summary>
    /// <remarks>
    /// For a C++ object that is its vtable, which is the cheapest test there is for "does
    /// this pointer still point at what it used to".
    /// </remarks>
    public ShellcodeBuilder MovEcxFromEax() => Bytes([0x8B, 0x08]);

    /// <summary><c>cmp ecx, imm32</c>.</summary>
    public ShellcodeBuilder CmpEcx(uint value) => Bytes([0x81, 0xF9]).Dword(value);

    /// <summary><c>mov eax, dword ptr [ecx+displacement]</c> — a field of the object in ecx.</summary>
    public ShellcodeBuilder MovEaxFromEcx(byte displacement = 0) =>
        displacement == 0 ? Bytes([0x8B, 0x01]) : Bytes([0x8B, 0x41, displacement]);

    /// <summary><c>mov dword ptr [ecx], eax</c>.</summary>
    public ShellcodeBuilder MovEcxPtrFromEax() => Bytes([0x89, 0x01]);

    /// <summary><c>add eax, imm32</c>.</summary>
    public ShellcodeBuilder AddEax(uint value) => Byte(0x05).Dword(value);

    /// <summary><c>cmp byte ptr [eax+displacement], imm8</c>.</summary>
    /// <remarks>
    /// A field of an object in hand, without loading it into anything. The one-byte
    /// displacement form covers any offset a record of a few hundred bytes has.
    /// </remarks>
    public ShellcodeBuilder CmpBytePtrEax(byte displacement, byte value) =>
        Bytes([0x80, 0x78, displacement, value]);

    /// <summary>
    /// <c>lea eax, [esi+displacement]</c> — an address relative to one already in hand.
    /// </summary>
    /// <remarks>
    /// How position-independent code reaches its own data: <c>call $+5; pop esi</c> leaves
    /// esi holding where the code ended up, and everything after is measured from there.
    /// The four-byte displacement is deliberate — the one-byte form saves three bytes and
    /// stops working at 127, which is well inside the length of a message a player types.
    /// </remarks>
    public ShellcodeBuilder LeaEaxEsi(int displacement) =>
        Bytes([0x8D, 0x86]).Dword(unchecked((uint)displacement));

    /// <summary><c>cmp eax, imm32</c>.</summary>
    public ShellcodeBuilder CmpEax(uint value) => Byte(0x3D).Dword(value);

    /// <summary><c>test al, al</c>.</summary>
    public ShellcodeBuilder TestAlAl() => Bytes([0x84, 0xC0]);

    /// <summary><c>test eax, eax</c>.</summary>
    public ShellcodeBuilder TestEaxEax() => Bytes([0x85, 0xC0]);

    /// <summary><c>test ax, imm16</c> — for a flag in the low half of a return value.</summary>
    public ShellcodeBuilder TestAx(ushort mask) => Bytes([0x66, 0xA9]).Word(mask);

    /// <summary><c>xor eax, eax</c>.</summary>
    public ShellcodeBuilder XorEaxEax() => Bytes([0x33, 0xC0]);

    /// <summary><c>cmp eax, dword ptr [address]</c>.</summary>
    public ShellcodeBuilder CmpEaxPtr(GameAddress address) => Bytes([0x3B, 0x05]).Dword(address.Value);

    /// <summary><c>push ebx</c>.</summary>
    public ShellcodeBuilder PushEbx() => Byte(0x53);

    /// <summary><c>pop ebx</c>.</summary>
    public ShellcodeBuilder PopEbx() => Byte(0x5B);

    /// <summary><c>xor ebx, ebx</c>.</summary>
    public ShellcodeBuilder XorEbxEbx() => Bytes([0x31, 0xDB]);

    /// <summary><c>inc ebx</c>.</summary>
    public ShellcodeBuilder IncEbx() => Byte(0x43);

    /// <summary><c>cmp ebx, imm32</c>.</summary>
    public ShellcodeBuilder CmpEbx(uint value) => Bytes([0x81, 0xFB]).Dword(value);

    /// <summary>
    /// <c>push dword ptr [esp+displacement]</c> — copies something already on the stack.
    /// </summary>
    /// <remarks>
    /// How a detour hands its caller's arguments to the function it stands in front of.
    /// Each push moves <c>esp</c>, so a run of these at one displacement walks backwards
    /// through the arguments and leaves them in the right order for the call.
    /// </remarks>
    public ShellcodeBuilder PushStackSlot(byte displacement) => Bytes([0xFF, 0x74, 0x24, displacement]);

    /// <summary><c>ret imm16</c> — returns and takes the callee's arguments off the stack.</summary>
    public ShellcodeBuilder RetAndPop(ushort bytes) => Byte(0xC2).Word(bytes);

    /// <summary><c>test edx, edx</c>.</summary>
    public ShellcodeBuilder TestEdxEdx() => Bytes([0x85, 0xD2]);

    /// <summary><c>call eax</c>.</summary>
    public ShellcodeBuilder CallEax() => Bytes([0xFF, 0xD0]);

    /// <summary><c>ret</c>.</summary>
    public ShellcodeBuilder Ret() => Byte(0xC3);

    /// <summary>
    /// <c>call dword ptr [address]</c> — an indirect call through a fixed slot.
    /// </summary>
    /// <remarks>
    /// How the client reaches its own imports. Calling the slot rather than what is in it
    /// survives the loader putting something else there, which is the point of the slot.
    /// </remarks>
    public ShellcodeBuilder CallIndirect(GameAddress slot) => Bytes([0xFF, 0x15]).Dword(slot.Value);

    /// <summary><c>push dword ptr [address]</c>.</summary>
    public ShellcodeBuilder PushPtr(GameAddress address) => Bytes([0xFF, 0x35]).Dword(address.Value);

    /// <summary><c>push imm8</c>, sign-extended to a dword by the CPU.</summary>
    public ShellcodeBuilder PushImm8(byte value) => Bytes([0x6A, value]);

    /// <summary><c>push imm32</c>.</summary>
    public ShellcodeBuilder PushImm32(uint value) => Byte(0x68).Dword(value);

    /// <inheritdoc cref="PushImm32(uint)"/>
    public ShellcodeBuilder PushImm32(GameAddress address) => PushImm32(address.Value);

    /// <summary><c>cld</c> — clears the direction flag so string ops move forward.</summary>
    public ShellcodeBuilder Cld() => Byte(0xFC);

    /// <summary><c>rep movsb</c> — copies <c>ecx</c> bytes from <c>esi</c> to <c>edi</c>.</summary>
    public ShellcodeBuilder RepMovsb() => Bytes([0xF3, 0xA4]);

    /// <summary><c>rep movsd</c> — copies <c>ecx</c> dwords from <c>esi</c> to <c>edi</c>.</summary>
    /// <remarks>
    /// Needs the direction flag cleared first, which inside somebody else's function is
    /// not something to assume: see <see cref="Cld"/>.
    /// </remarks>
    public ShellcodeBuilder RepMovsd() => Bytes([0xF3, 0xA5]);

    /// <summary>
    /// <c>repne scasd</c> — searches <c>ecx</c> dwords at <c>edi</c> for <c>eax</c>.
    /// </summary>
    /// <remarks>
    /// Leaves the zero flag set when it found one, so the branch that follows is a
    /// <c>jne</c> for "not in the table". Needs the direction flag cleared first.
    /// </remarks>
    public ShellcodeBuilder RepneScasd() => Bytes([0xF2, 0xAF]);

    /// <summary><c>add esp, imm8</c> — cleans up a cdecl call's arguments.</summary>
    public ShellcodeBuilder AddEsp(byte bytes) => Bytes([0x83, 0xC4, bytes]);

    /// <summary>
    /// <c>call rel32</c> to an absolute address, with the displacement computed from
    /// where this instruction will actually sit.
    /// </summary>
    public ShellcodeBuilder CallTo(GameAddress target)
    {
        Byte(CallRel32);
        var nextInstruction = BaseAddress + (Length + Rel32Size);
        return Dword(unchecked((uint)(target - nextInstruction)));
    }

    /// <summary>
    /// <c>jmp rel32</c> to an absolute address, with the displacement computed from
    /// where this instruction will actually sit.
    /// </summary>
    public ShellcodeBuilder JumpTo(GameAddress target)
    {
        Byte(JmpRel32);
        var nextInstruction = BaseAddress + (Length + Rel32Size);
        return Dword(unchecked((uint)(target - nextInstruction)));
    }

    /// <summary>
    /// Emits a short conditional branch with an unresolved target. Pass the returned
    /// label to <see cref="MarkLabel"/> at the destination.
    /// </summary>
    /// <param name="conditionOpcode">
    /// The <c>Jcc rel8</c> opcode, for example <c>0x75</c> for <c>jne</c>.
    /// </param>
    public Label ShortJump(byte conditionOpcode)
    {
        Byte(conditionOpcode);
        var displacementIndex = _code.Count;
        Byte(0);
        return new Label(displacementIndex);
    }

    /// <summary><c>jne rel8</c>, target resolved later by <see cref="MarkLabel"/>.</summary>
    public Label ShortJumpIfNotEqual() => ShortJump(0x75);

    /// <summary><c>jz rel8</c>, target resolved later by <see cref="MarkLabel"/>.</summary>
    public Label ShortJumpIfZero() => ShortJump(0x74);

    /// <summary><c>jmp rel8</c>, target resolved later by <see cref="MarkLabel"/>.</summary>
    public Label ShortJumpAlways() => ShortJump(0xEB);

    /// <summary><c>jge rel8</c>, target resolved later by <see cref="MarkLabel"/>.</summary>
    public Label ShortJumpIfGreaterOrEqual() => ShortJump(0x7D);

    /// <summary><c>jnz rel8</c>, target resolved later by <see cref="MarkLabel"/>.</summary>
    /// <remarks>The same opcode as <see cref="ShortJumpIfNotEqual"/>, read the other way.</remarks>
    public Label ShortJumpIfNotZero() => ShortJump(0x75);

    /// <summary>A branch with a four-byte displacement whose target is not known yet.</summary>
    /// <remarks>
    /// The counterpart of <see cref="Label"/> for targets further than a signed byte away.
    /// Worth the extra bytes only where the distance demands it — which in a cave of any
    /// size means the shared exit, and little else.
    /// </remarks>
    public readonly record struct NearLabel(int DisplacementIndex);

    /// <summary>
    /// Emits a conditional branch with a four-byte displacement.
    /// </summary>
    /// <param name="conditionOpcode">
    /// The second byte of the <c>0F 8x</c> form — <c>0x82</c> for <c>jb</c>, <c>0x84</c> for
    /// <c>jz</c>, <c>0x87</c> for <c>ja</c>.
    /// </param>
    public NearLabel NearJump(byte conditionOpcode)
    {
        Bytes([0x0F, conditionOpcode]);
        return ReserveDisplacement();
    }

    /// <summary><c>jmp rel32</c>, target resolved later by <see cref="MarkLabel(NearLabel)"/>.</summary>
    public NearLabel NearJumpAlways()
    {
        Byte(JmpRel32);
        return ReserveDisplacement();
    }

    private NearLabel ReserveDisplacement()
    {
        var index = _code.Count;
        Dword(0);
        return new NearLabel(index);
    }

    /// <summary>Resolves a four-byte branch emitted earlier to the current position.</summary>
    public ShellcodeBuilder MarkLabel(NearLabel label)
    {
        BinaryPrimitives.WriteInt32LittleEndian(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_code)
                .Slice(label.DisplacementIndex, Rel32Size),
            _code.Count - label.DisplacementIndex - Rel32Size);

        return this;
    }

    /// <summary>A position already emitted, for a branch that jumps back to it.</summary>
    public readonly record struct Anchor(int Offset);

    /// <summary>Marks the current position as the target of a later backward branch.</summary>
    public Anchor Here() => new(Length);

    /// <summary>
    /// Emits a short branch back to <paramref name="anchor"/>.
    /// </summary>
    /// <remarks>
    /// The backward counterpart of <see cref="ShortJump"/>: the target is already known,
    /// so the displacement is resolved immediately rather than patched in later.
    /// </remarks>
    /// <param name="conditionOpcode">The <c>Jcc rel8</c> opcode, or <c>0xEB</c> for an unconditional jump.</param>
    /// <exception cref="InvalidOperationException">The branch is out of <c>rel8</c> range.</exception>
    public ShellcodeBuilder ShortJumpBack(byte conditionOpcode, Anchor anchor)
    {
        Byte(conditionOpcode);
        var displacement = anchor.Offset - (Length + 1);

        if (displacement is < sbyte.MinValue or > sbyte.MaxValue)
        {
            throw new InvalidOperationException(
                $"Short branch displacement {displacement} does not fit in a signed byte; use a near jump.");
        }

        return Byte(unchecked((byte)(sbyte)displacement));
    }

    /// <summary>
    /// Emits a branch back to <paramref name="anchor"/> with a four-byte displacement.
    /// </summary>
    /// <remarks>
    /// The backward counterpart of <see cref="NearJump"/>, for a loop whose body is longer
    /// than a signed byte can reach — which any loop with a call in it tends to be.
    /// </remarks>
    /// <param name="conditionOpcode">The second byte of the <c>0F 8x</c> form.</param>
    public ShellcodeBuilder NearJumpBack(byte conditionOpcode, Anchor anchor)
    {
        Bytes([0x0F, conditionOpcode]);

        return Dword(unchecked((uint)(anchor.Offset - (Length + Rel32Size))));
    }

    /// <inheritdoc cref="NearJumpBack"/>
    public ShellcodeBuilder NearJumpBackAlways(Anchor anchor)
    {
        Byte(JmpRel32);

        return Dword(unchecked((uint)(anchor.Offset - (Length + Rel32Size))));
    }

    /// <summary>Resolves a label emitted earlier to the current position.</summary>
    /// <exception cref="InvalidOperationException">The branch is out of <c>rel8</c> range.</exception>
    public ShellcodeBuilder MarkLabel(Label label)
    {
        var displacement = _code.Count - label.DisplacementIndex - 1;
        if (displacement is < 0 or > sbyte.MaxValue)
        {
            throw new InvalidOperationException(
                $"Short branch displacement {displacement} does not fit in a signed byte; use a near jump.");
        }

        _code[label.DisplacementIndex] = (byte)displacement;
        return this;
    }

    /// <summary>The finished machine code.</summary>
    public byte[] Build() => [.. _code];
}
