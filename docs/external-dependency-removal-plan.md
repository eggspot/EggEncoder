# External Dependency Removal Plan

EggEncoder's product code (`src/EggEncoder/EggEncoder.csproj`) currently references six NuGet
packages. **The owner's decision: every one of them that carries actual codec implementation code
gets replaced with a from-scratch, pure-C# EggEncoder implementation; the two that carry zero
implementation code (pure interface contracts for .NET ecosystem interop) are kept as an explicit,
documented exception rather than "rewritten"** — see "Decision: DI/Logging abstractions" below for
why that one isn't a rewrite candidate at all.

This is a multi-session, multi-PR initiative, the same shape as
`docs/managed-codec-rewrite-plan.md` (which removed every *native* dependency; this plan removes
every remaining *managed NuGet* one). **This file is the source of truth for what's done, what's
next, and why** — read it whole before picking anything, the same convention that file and
`docs/video-support-backlog.md` already establish.

## How to use this plan (read this first, every time)

1. **Read this whole file once**, including the audit table and the "what this plan deliberately
   does not decide" section, before picking anything.
2. **Read the repo's own `CLAUDE.md`** for house conventions (feature branch + PR, never push to
   `main`, conventional commits, 100% branch coverage on new code, `xUnit` + `FluentAssertions` +
   AAA, `Feature_Condition_ExpectedBehavior` test naming, docs-sync in the same PR).
3. **Check `git log --oneline -20`, `gh pr list --state all`, and this file's checkboxes together**
   before picking anything — continue an unmerged branch/open PR from a prior session before
   starting something new.
4. **Pick the first unchecked item whose "Depends on" line (if any) is already checked off.** Within
   the MP3 decoder phase, sub-items have a real order (core decode before joint stereo before
   block switching before MPEG-2/2.5, and the actual cutover/dependency-removal step only once every
   sub-item is done and verified). Across codecs (MP3 vs. Vorbis vs. Opus), items are independent
   and can be picked in any order.
5. **Preserve the exact public API** each replaced type exposes today (see the audit table) —
   signatures, defaults, thrown exception types/conditions. This is a swap of what's *behind* the
   API, not a breaking change to it, exactly like `managed-codec-rewrite-plan.md`'s own rule.
6. **The replaced dependency stays in the `.csproj` until its own cutover sub-item is done and
   merged.** Don't remove a `PackageReference` in the same PR that adds the new implementation —
   land the new code first, verified against real fixtures with the old dependency still present
   as the fallback/oracle, then remove the dependency as its own small, easy-to-revert PR. This
   mirrors how every native binary in `managed-codec-rewrite-plan.md` was removed only after its
   replacement was already merged and verified (see that file's own item 8 and its own "do not
   touch the cleanup item until every other item is checked off" rule).
7. **Verification bar**: for a lossy codec (MP3/Vorbis/Opus decode or encode), round-trip through
   this project's own decoder (once it exists) and the existing real-world fixtures already in the
   repo, measuring SNR the same way `Mp3EncoderSnrTest`/`VorbisEncoderSessionTest` already do — not
   bit-exact output, since more than one valid encoding/decoding of the same audio exists for a
   lossy codec. For *decode* specifically, the real bar is **decodes real-world files correctly**,
   not just files this project's own (deliberately simplified) encoders produce — see the MP3
   decode phase's own scope note for why that distinction matters here specifically.
8. **Commit to a feature branch, open a PR, self-review (two passes, per `CLAUDE.md`), merge, run
   `dotnet test --configuration Release` on `main`.**
9. **Check the item off in this file in the same PR**, with a one-line status note (merged PR link
   + anything genuinely left out of scope), the same format `managed-codec-rewrite-plan.md`'s own
   checked items use.

## Decision: DI/Logging abstractions are a permanent, documented exception, not a rewrite candidate

`Microsoft.Extensions.DependencyInjection.Abstractions` and `Microsoft.Extensions.Logging.Abstractions`
back the optional `ServiceCollectionExtensions.AddEggEncoder` DI registration and `NativeEncoder`'s
optional logging. Unlike the four codec dependencies below, **neither package contains any
implementation code at all** — they are pure interface/type contracts (`IServiceCollection`,
`ILogger<T>`, etc.) that the wider .NET ecosystem (ASP.NET Core and every DI/logging container that
implements them) also depends on directly. "Rewriting" `IServiceCollection` ourselves would not
produce a drop-in replacement — it would produce a *different, incompatible type* that no real
host application's own container implements, defeating the entire point of the integration (a
caller registering EggEncoder into their own, real ASP.NET Core `IServiceCollection`). The owner's
explicit decision: keep both as a documented exception (see `THIRD-PARTY-NOTICES.md`'s own entry)
rather than drop the DI/logging integration feature or ship a non-interoperable lookalike. No
backlog item exists for them; this section is the permanent record of that decision.

## Audit — every remaining NuGet dependency in product code, and what replaces it

| Package | Used by | What it does | Replacement |
|---|---|---|---|
| `Concentus` | `Codecs/Opus/OpusEncoderSession.cs`, `Codecs/Opus/OpusDecoder.cs` | Full Opus codec (SILK speech + CELT music layers, RFC 6716) | Phase 4 below |
| `NLayer` | `Codecs/Mp3/Mp3Decoder.cs` | MPEG-1/2/2.5 Layer III decode | Phase 1 below |
| `NVorbis` | `Codecs/Vorbis/VorbisDecoder.cs` | Ogg Vorbis decode | Phase 2 below |
| `OggVorbisEncoder` | `Codecs/Vorbis/VorbisEncoderSession.cs` | Ogg Vorbis encode | Phase 3 below |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | `ServiceCollectionExtensions.cs` | `IServiceCollection` contract | Kept — see decision above |
| `Microsoft.Extensions.Logging.Abstractions` | `NativeEncoder.cs` | `ILogger<T>` contract | Kept — see decision above |

Phases are ordered by how self-contained/tractable each one is, cheapest first — **not** by how
"important" the format is. MP3 decode goes first because this project's own MP3 *encoder*
(`Codecs/Mp3/Mp3*.cs`) already supplies the Huffman code tables, the polyphase window
coefficients, and a proven `Transform.Mdct`/`Transform.BitWriter`/`Transform.BitReader`
infrastructure to build on (see Phase 1's own "what's reusable" note) — real head start, even
though the decode-specific logic itself (scalefactors, count1, bit reservoir, joint stereo, block
switching) is still new work the encoder never needed. Opus goes last: SILK+CELT together are the
largest, most intricate single codec in this list, with the least overlap with anything already in
this codebase.

---

## Phase 1 — MP3 decode (replaces `NLayer`)

### What's reusable from the existing in-house MP3 encoder (and what genuinely isn't)

**Reusable directly:**
- `Mp3HuffmanTables` — the big_values Huffman tables (table_select 1-31) and the count1 quadruple
  tables (A/B), plus `GetBigValueTable(tableSelect)` for `(table, width, linbits)`.
  `Transform.HuffmanTable.Decode(BitReader)` (already used by AAC decode) gives the code→index
  direction directly; unflatten the same `x*width+y` (big_values) / `v*8+w*4+x*2+y` (count1)
  convention the encoder's own doc comments already document.
- `Mp3Tables.WindowCoefficients` — the 512-tap analysis/synthesis window, explicitly documented as
  shared between the encoder's analysis filter and the decoder's synthesis filter.
- `Mp3Tables.AliasReductionCoefficients`/`AliasCs`/`AliasCa` — decode needs the *inverse* alias
  reduction (restoring aliasing the encoder's forward butterfly introduced); since the forward
  butterfly is an orthogonal rotation (`Cs² + Ca² == 1`), the inverse is its transpose — a cheap,
  well-defined derivation from the same coefficients, not a new table.
- `Mp3Probe`'s frame-header parsing (sync word, version/layer/bitrate-index/sample-rate-index/mode,
  frame length, side-info size) and its MPEG-1/2/2.5 bitrate/sample-rate tables (`Mpeg1Layer3BitrateKbps`
  etc., already `internal` for exactly this kind of reuse).
- `Transform.Mdct.Inverse` — already a generic, arbitrary-even-length IMDCT (confirmed in use by
  `AacFrameDecoder`'s own windowed-overlap-add pattern, which this phase's own hybrid-filter step
  mirrors directly: `Mdct.Inverse(18 coefficients)` → 36 time-domain samples → multiply by the
  long-block sine window → overlap-add with the previous granule's second half).
- `Transform.BitReader`/`Transform.BitWriter` — MSB-first, matching MP3's own bitstream convention
  (confirmed already, no new bit reader needed).

**Genuinely new** (the encoder's own deliberately-simplified MVP scope never needed any of this —
see `Mp3Quantizer.cs`'s own doc comment: "never uses per-band scalefactors... or the count1
region", `Mp3FrameEncoder.cs`'s own "independent (non-joint) stereo, no bit-reservoir borrowing"):
real per-band scalefactor decode (`scalefac_compress` → `slen1`/`slen2`, `scfsi`), count1 region
decode, real bit-reservoir frame assembly (`main_data_begin` can be nonzero and borrow from a
previous frame's leftover bits), joint stereo (MS and intensity) reconstruction, and short/mixed
block decode (block-switching, reorder, the three-short-window shape, `subblock_gain`).

### Why this phase targets full real-world decode compatibility, not just "whatever our own encoder writes"

`NLayer` today decodes any real-world MPEG-1/2/2.5 Layer III file. The in-house encoder's own
simplified scope (no scalefactors, no joint stereo, no short blocks, no real bit reservoir) is
*not* a safe scope to copy for the decoder — a decoder limited to that subset would fail or
mis-decode a large fraction of real-world MP3s (anything LAME/ffmpeg produced with their own
defaults, which routinely use all four of those features). **The owner's explicit decision:**
match `NLayer`'s real-world compatibility, built incrementally via the sub-items below, not a
narrower "decodes our own output" scope.

- [ ] **1a. MPEG-1 Layer III core decode** — frame header + side info parsing (real
  `main_data_begin`, a rolling bit-reservoir buffer of previous frames' unused main data bytes),
  real scalefactor decode (`scalefac_compress` 0-15 → `slen1`/`slen2` via the spec's own published
  table, `scfsi` for granule 1 reusing granule 0's scalefactors per band when set), `big_values` +
  `count1` Huffman decode (reusing `Mp3HuffmanTables` per the note above), full requantization
  (`global_gain`, `scalefac_scale`, `preflag`/pretab — not just the encoder's own simplified
  `global_gain`-only formula), long-block-only hybrid filter (IMDCT + sine window + overlap-add),
  frequency inversion, polyphase synthesis filter bank. **Independent (non-joint) stereo only**
  and **long blocks only** for this sub-item — joint stereo and block switching are 1b/1c.
  - **Depends on**: nothing.
  - **Acceptance**: decodes every long-block, independent-stereo region of the existing real
    fixtures (`cbr128.mp3`, `no_xing.mp3`, `tone.mp3`, `vbr_q4.mp3` — audit which regions of each
    actually qualify before relying on them end-to-end) with correct channel count/sample rate and
    SNR at least as good as this project's own lossy-codec floor elsewhere; throws a clear,
    specific `NotSupportedException` (not silent wrong output) for any frame this sub-item doesn't
    yet cover (joint stereo mode bits set, or `window_switching_flag` indicating a short/mixed
    block), so 1b/1c have an unambiguous "not implemented yet" signal to replace rather than a
    silent correctness gap.
- [ ] **1b. Joint stereo (MS + intensity) reconstruction** — decodes `mode_extension` for both
  joint-stereo bits (MS stereo's sum/difference reconstruction per channel, intensity stereo's
  scalefactor-band-driven panning reconstruction above the `intensity_stereo_bound`).
  - **Depends on**: 1a.
- [ ] **1c. Short & mixed block decode** — `window_switching_flag`/`block_type`/`mixed_block_flag`,
  the three-short-window shape + `subblock_gain` in requantization, reorder (short-block
  scalefactor-band coefficients are coded in a different frequency/window interleaving than long
  blocks and must be reordered before the hybrid filter), and the alternate (short) window shape
  for the hybrid filter's own IMDCT+overlap-add.
  - **Depends on**: 1a (can be developed in parallel with 1b; both depend only on 1a).
- [ ] **1d. MPEG-2/2.5 (LSF) support** — one granule per frame instead of two, the LSF-specific
  scalefactor compression table (different from MPEG-1's), no `scfsi`. `Mp3Probe` already detects
  MPEG-2/2.5 headers (`_mpeg2SampleRates`/`_mpeg25SampleRates`/`_mpeg2Layer3BitrateKbps`); decode
  does not yet need to.
  - **Depends on**: 1a (can be developed in parallel with 1b/1c).
- [ ] **1e. Cutover — swap `Mp3Decoder.Decode` to the in-house decoder, remove `NLayer`** — only
  once 1a-1d are all merged and verified. Re-run every existing MP3 test (`Mp3DecoderTest`,
  `Mp3ProbeTest`, `Mp3EncoderSnrTest`, `AotSmokeTest`'s own MP3 round trip) against the new decoder
  before removing the `PackageReference`; add a regression test per real fixture confirming
  identical channel count/sample rate/duration and comparable SNR to what `NLayer` reported, so a
  silent regression at cutover time is caught by tests, not discovered later.
  - **Depends on**: 1a, 1b, 1c, 1d.

---

## Phase 2 — Vorbis decode (replaces `NVorbis`)

Vorbis decode (packet framing already handled by this project's own Ogg container work for
Opus/`Codecs/Opus/OggPageReader.cs`, though Vorbis's own container handling today is fully owned by
NVorbis internally per `CLAUDE.md`'s own note — confirm at implementation time whether
`OggPageReader` is reusable here or Vorbis's own packet-boundary conventions differ enough to need
their own reader) needs: the Vorbis codebook format (`VorbisCodebook` abstraction, canonical or
explicit Huffman-like codeword lengths, scalar or vector quantized codebook entries), the floor
decode (floor type 0, LSP-based, or floor type 1, a GUI-like piecewise linear curve — real-world
encoders use floor 1 almost exclusively, confirm which this project's own `VorbisEncoderSession`'s
own dependency produces before scoping), residue decode (types 0/1/2, partitioned
classification+codebook-coded spectral residue), inverse MDCT with Vorbis's own window function,
and channel coupling (stereo and beyond). Not yet broken into sub-items — do that as this phase's
own first step, following this plan's own "read whole, then scope the real sub-items" convention,
once Phase 1 is done and real implementation experience from it (e.g. how MDCT/bit-reservoir-style
work actually went) can inform the breakdown.

- [ ] **2. Scope and implement Vorbis decode** — replaces `VorbisDecoder.Decode`.
  - **Depends on**: nothing (independent of Phase 1).

## Phase 3 — Vorbis encode (replaces `OggVorbisEncoder`)

Natural follow-up to Phase 2 — a from-scratch Vorbis encoder needs the same codebook/floor/residue
machinery in the opposite direction, the same "decode informs encode" relationship
`managed-codec-rewrite-plan.md`'s own FLAC/MP3 items already had. Scope once Phase 2 is merged.

- [ ] **3. Scope and implement Vorbis encode** — replaces `VorbisEncoderSession`.
  - **Depends on**: Phase 2 (for the shared codebook/floor/residue infrastructure; a genuinely
    separate from-scratch encode effort is possible but would duplicate that work for no reason).

## Phase 4 — Opus (replaces `Concentus`)

RFC 6716's SILK (speech, linear-prediction-based) and CELT (music, MDCT-based) layers, plus the
hybrid mode that splits a single frame's frequency range across both. The largest, most intricate
single item in this entire plan — deliberately scoped last so Phases 1-3's own real implementation
experience (bit-reservoir-style frame state, MDCT-based spectral decode, codebook-driven entropy
coding) is already in hand before starting the hardest one. Not yet broken into sub-items for the
same reason Phase 2 isn't — scope this phase's own first step once Phase 3 is done.

- [ ] **4. Scope and implement Opus (SILK + CELT + hybrid)** — replaces
  `OpusEncoderSession`/`OpusDecoder`.
  - **Depends on**: nothing structurally, but scheduled last by design (see above).

---

## What this plan deliberately does not decide

- **Exact SNR/decodability acceptance numbers for Phases 2-4** — set them when each phase is
  actually scoped (its own first step), the same way Phase 1's own numbers above are a starting
  point to confirm/adjust with real implementation experience, mirroring
  `managed-codec-rewrite-plan.md` item 6's own "confirm/adjust with the owner before starting"
  convention.
- **Whether Vorbis decode reuses `Codecs/Opus/OggPageReader`/`OggPageWriter` or needs its own** —
  confirmed at Phase 2's own scoping step, not assumed here.
- **Whether a reference implementation may be studied at arm's length for any item** — per
  `managed-codec-rewrite-plan.md`'s own precedent (WavPack's decorrelation/entropy algorithm, which
  has no public written spec), this requires the owner's explicit, item-specific sign-off each
  time, never assumed. MP3 (Phase 1), Vorbis (Phases 2-3), and Opus (Phase 4) all have complete,
  publicly published specifications (ISO/IEC 11172-3 and the Xiph Vorbis I spec / RFC 6716
  respectively), so the default expectation is spec-only, clean-room implementation, the same as
  this project's own FLAC/WavPack/MP3-encoder work already established — not an assumption that
  sign-off will be granted if requested.
