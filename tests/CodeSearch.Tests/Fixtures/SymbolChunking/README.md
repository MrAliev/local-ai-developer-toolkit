# Symbol-chunking fixtures

Source files whose only purpose is to be handed to `scip-typescript` and
`scip-python`, so that what those indexers emit for a definition can be checked
against the source rather than against the SCIP schema.

They exist for [#82](https://github.com/MrAliev/local-ai-developer-toolkit/issues/82)
— chunking TypeScript and Python by symbol instead of by a 60-line window — and
were written for its phase 0,
[#87](https://github.com/MrAliev/local-ai-developer-toolkit/issues/87): whether
the adapters emit `enclosing_range`, the field that gives the span of the body
of a definition rather than the span of its name.

## What each file is for

`typescript/` is one project (`tsconfig.json`) covering the shapes a naive
boundary gets wrong:

| File | Shapes |
| --- | --- |
| `rates.ts` | module-level `const`; exported function; the import target for the others |
| `shapes.ts` | imports and a re-export at the top; exported function; class with two methods; arrow function in a `const`; a stray statement after the last definition |
| `Panel.tsx` | React component with a `useMemo` and a helper function declared inside it; exported arrow component; a second component containing a nested component, a nested function and a nested class with a method |
| `internal.ts` | the same constructs *without* `export`, to separate "not exported" from "nested" |
| `types/react.d.ts` | a three-declaration stand-in for `react`, so the JSX compiles with no `node_modules` |

`python/shapes.py` covers a module-level function, a class with two methods, a
function nested inside a function, a decorated definition, and module-level code
after the last definition.

Both are deliberately tiny. They are read by eye against the report below, and
that only works while they stay small.

## Reproducing the measurement

```
cd typescript && scip-typescript index .
cd .. && python scip_enclosing_range_report.py typescript/index.scip typescript

cd python && scip-python index . --project-name fixture --project-version _
cd .. && python scip_enclosing_range_report.py python/index.scip python
```

`scip_enclosing_range_report.py` decodes the SCIP protobuf itself and needs
nothing installed. For every definition occurrence it prints the body span if
there is one, checks that a nested body sits inside its parent rather than
swallowing it, and lists the lines no definition body covers. `index.scip` is
build output and is ignored by git.

## What the run said, on 2026-08-20

Measured with `@sourcegraph/scip-typescript` 0.4.0 and
`@sourcegraph/scip-python` 0.6.6 on Node v20.20.2.

Both adapters emit `enclosing_range`. The raw share over all definition
occurrences — 38% for TypeScript, 36% for Python — says nothing, because most
definition occurrences are parameters, fields and local variables, which have no
body to describe. The share that matters is over definitions that have one:

- **Python: 8 of 8.** Every `def` and `class`, the nested function included. A
  decorated definition starts its body at the decorator, one line above the
  name. The module symbol carries no range.
- **TypeScript: 13 of 18.** Every definition that gets a global symbol has a
  body span, whether exported or not. The five without one are exactly the five
  declared inside a function body: `scip-typescript` gives those `local N`
  symbols, and a `local` never carries `enclosing_range`.

Two shapes need care from anything that consumes these ranges:

- For an arrow function in a `const`, the body span covers the arrow function
  expression only. It starts after `export const name = `, so it does **not**
  contain the occurrence of the name.
- The module symbol of a TypeScript document carries a body span covering the
  whole file. Treating it as a definition would give every file a whole-file
  chunk on top of its symbol chunks.
