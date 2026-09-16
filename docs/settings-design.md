# Settings dialog

## Design basis

[Microsoft's app settings guidance](https://learn.microsoft.com/en-us/windows/apps/design/app-settings/guidelines-for-app-settings)
recommends a single entry point for preferences, related settings grouped under
clear headings, readable descriptions, appropriate input controls, and immediate
feedback. Its full-window recommendation is specific to modern Windows apps;
DiffDino retains its cross-platform modal and shared dialog controls.

[Nielsen Norman Group's progressive disclosure guidance](https://www.nngroup.com/articles/progressive-disclosure/)
recommends exposing common choices first and revealing specialized options when
needed. This supports separating everyday preferences, the built-in agent, and
external agent connections rather than adding more controls to one scrolling list.

## Previous behavior

- A 480px-wide, 600px-high dialog held every section in one scroll region.
- Theme, language, scale, repository preferences, editor tools, keyboard shortcuts,
  and the local MCP server shared that list.
- The built-in assistant's provider, model, endpoint, and API key were only editable
  from chat. Its storage already remembered choices and credentials per provider.
- General preferences applied immediately, while assistant changes required Save.

## Implemented changes

- Use the existing 600px wide-dialog size with four persistent tabs: General,
  Keyboard, Agent, and Agent connections. The title, close control, and navigation stay above
  the scrolling content. Each page retains its scroll position and controls.
  Categories use natural-width labels and an underline, without file-tab dividers
  or ellipsis that can truncate fitting labels at fractional display scales.
- Keep existing general preferences and MCP controls, with descriptions explaining
  their scope and immediate application.
- Put the searchable shortcut editor directly in Keyboard, with search and reset
  controls pinned around its scrolling list. Search survives tab changes; leaving
  a tab releases its keyboard focus and cancels any unfinished shortcut recording.
- Embed the existing assistant configuration form in Agent, including provider
  readiness labels, model presets and custom names, conditional endpoint input,
  masked keys, and environment-key guidance.
- Share the assistant session store, but give the dialog its own disposable editor.
  Opening or resetting settings does not open chat or overwrite its pending edits.
  The page works even without a repository.
- Explain that Save applies the selected provider and stores its choices. Other
  providers' saved choices remain available. Save each provider before switching.
  Reset reloads saved values; closing abandons unapplied agent edits. Changing
  settings tabs preserves them. Enter alone does not dismiss or save the dialog.
- Show the selected provider and model so changes have a visible result, and use
  existing credential storage rather than introducing another key store.
- Translate new copy into all seven supported languages.

## Scope and follow-up

Four categories keep the current setting count small enough to browse without a
global search field. Revisit search when more settings are added. Endpoint validation,
connection testing, and unsaved drafts across provider switches would improve the
shared chat editor too, and should be implemented there once for both surfaces.

The dialog adds no credential persistence or network boundary. Its local editor
shares the existing store intentionally; regression tests cover key isolation,
chat draft independence, category navigation, masking, dismissal, and layout.

## GPU border regression

At fractional display scales, a rectangle edge can land on a physical pixel center.
The rectangle shaders formerly treated that edge as outside the fill even when its
border width was zero, then borrowed the bottom border's color. This produced an
L-shaped selection underline depending on the tab's position. Both OpenGL and Metal
now test only sides with a positive border width in the straight-edge fill calculation.

Interpolating local coordinates also made an inner border edge vary slightly along
one pixel row, so the underline could be thinner at one end. Both shaders now
reconstruct canvas coordinates from fragment pixel centers and flat rectangle data.
The host supplies logical and rounded framebuffer sizes; Metal converts its top-down
fragment coordinates to the canvas's bottom-up convention. Metal's uniform buffer
retains the projection at byte 0 and appends the four size values at byte 64, leaving
the other shaders' projection layout intact.

The software rasterizer does not execute these shaders. The opt-in GPU checks use
OpenGL 4.1 on Windows/Linux and an offscreen Metal texture on macOS. They render all
four settings tabs at every supported UI scale, both hovered and unhovered, and
read back pixels to check that the underline has uniform width and no vertical edges.
The Metal path compiles the shipping shaders and exercises the actual uniform layout;
it must be run on a Mac to verify native Metal compilation and rendering.

```powershell
$env:DIFFDINO_GPU_TESTS = '1'
dotnet test GitBench.Tests/GitBench.Tests.csproj --filter 'FullyQualifiedName~CategoryUnderlinesAreUniformOnGpu'
```
