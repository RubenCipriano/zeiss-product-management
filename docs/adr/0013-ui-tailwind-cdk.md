# 0013 — UI with Tailwind CSS and Angular CDK

**Status:** accepted (supersedes Angular Material)

## Context

Angular Material gave a consistent but generic look and was hard to restyle. A more modern, brand-able design was
requested, without giving up accessibility (focus management, keyboard navigation, ARIA).

## Decision

* **Tailwind CSS v4** (added with `ng add tailwindcss`) for styling. Design tokens (brand colours, font) live in
  `@theme` in `src/tailwind.css`. A small set of primitives (`.btn`, `.input`, `.card`, `.menu`, `.dialog-panel`) keeps
  buttons, fields and cards identical everywhere; everything else is utility classes in the templates.
* **Angular CDK** (Material's unstyled behaviour layer) for the hard interactive parts: `Dialog` (focus trap, Esc,
  backdrop, `aria-modal`), `Menu` (keyboard navigation, roles). The look comes from Tailwind.
* Small in-house components where a library would be overkill: inline SVG `Icon`, server-side `Paginator`, signal-based
  toast notifications.

## Consequences

* Smaller bundles: initial 121 kB → 91 kB gzip before sign-in support (107 kB with `oidc-client-ts`); the product
  page went from 72 kB to 40 kB.
* We own a few components that Material provided (paginator, toasts). They are small and covered by tests.
* Styling is co-located with markup; reviewers see the design in the template without jumping to SCSS files.
