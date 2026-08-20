"""Report what share of SCIP definition occurrences carry `enclosing_range`.

Phase 0 of the symbol-level chunking plan turns on one fact: whether an external
indexer gives us the span of the body of a definition, and not only the span of
its name. This script answers that from a real index rather than from the schema.

    cd typescript && scip-typescript index .
    python ../scip_enclosing_range_report.py typescript/index.scip typescript

    cd python && scip-python index . --project-name fixture --project-version _
    python ../scip_enclosing_range_report.py python/index.scip python

It reads the protobuf wire format directly, so it needs nothing installed. Only
the fields the question needs are decoded; everything else is skipped by length.
Neither adapter sets `SymbolInformation.kind`, so definitions are classified by
the hover documentation both of them do emit.
"""

import os
import sys
from collections import defaultdict

DEFINITION_ROLE = 0x1


class Reader:
    def __init__(self, buf):
        self.buf = buf
        self.pos = 0

    def end(self):
        return self.pos >= len(self.buf)

    def varint(self):
        shift = value = 0
        while True:
            byte = self.buf[self.pos]
            self.pos += 1
            value |= (byte & 0x7F) << shift
            if not byte & 0x80:
                return value
            shift += 7

    def field(self):
        key = self.varint()
        return key >> 3, key & 7

    def payload(self, wire):
        if wire == 2:
            size = self.varint()
            out = self.buf[self.pos:self.pos + size]
            self.pos += size
            return out
        if wire == 0:
            return self.varint()
        if wire == 5:
            self.pos += 4
        elif wire == 1:
            self.pos += 8
        else:
            raise ValueError("unsupported wire type %d" % wire)
        return None


def ints(buf):
    reader, out = Reader(buf), []
    while not reader.end():
        out.append(reader.varint())
    return out


def span(values):
    """SCIP ranges are [startLine, startChar, endLine, endChar], or three values
    when the range does not cross a line."""
    if len(values) == 3:
        return values[0], values[1], values[0], values[2]
    return tuple(values[:4])


def occurrence(buf):
    reader = Reader(buf)
    out = {"range": [], "symbol": "", "roles": 0, "enclosing": None}
    while not reader.end():
        field, wire = reader.field()
        value = reader.payload(wire)
        if field == 1:
            out["range"].extend(ints(value) if wire == 2 else [value])
        elif field == 2 and wire == 2:
            out["symbol"] = value.decode("utf-8", "replace")
        elif field == 3 and wire == 0:
            out["roles"] = value
        elif field == 7:
            out["enclosing"] = (out["enclosing"] or []) + (
                ints(value) if wire == 2 else [value])
    return out


def symbol_information(buf):
    reader = Reader(buf)
    out = {"symbol": "", "documentation": []}
    while not reader.end():
        field, wire = reader.field()
        value = reader.payload(wire)
        if field == 1 and wire == 2:
            out["symbol"] = value.decode("utf-8", "replace")
        elif field == 3 and wire == 2:
            out["documentation"].append(value.decode("utf-8", "replace"))
    return out


def document(buf):
    reader = Reader(buf)
    out = {"path": "", "occurrences": [], "symbols": []}
    while not reader.end():
        field, wire = reader.field()
        value = reader.payload(wire)
        if field == 1 and wire == 2:
            out["path"] = value.decode("utf-8", "replace")
        elif field == 2 and wire == 2:
            out["occurrences"].append(occurrence(value))
        elif field == 3 and wire == 2:
            out["symbols"].append(symbol_information(value))
    return out


def read_index(path):
    with open(path, "rb") as handle:
        reader = Reader(handle.read())
    documents = []
    while not reader.end():
        field, wire = reader.field()
        value = reader.payload(wire)
        if field == 2 and wire == 2:
            documents.append(document(value))
    return documents


def classify(symbol, hover):
    # Both adapters leave SymbolInformation.kind unset, so the hover markdown is
    # the only thing that says what a definition is. It is fenced code, one
    # declaration per line, so flattening the whitespace makes it greppable.
    text = " %s " % " ".join(hover).replace("`", " ").replace("\n", " ")
    if symbol.endswith(":") or " module " in text or "(module)" in text:
        return "module"
    if "(method)" in text:
        return "method"
    if "(parameter)" in text or "().(" in symbol:
        return "parameter"
    if "(property)" in text or "(variable)" in text:
        return "field"
    if " def " in text or " function " in text:
        return "function"
    if " class " in text or " interface " in text:
        return "class"
    if " var " in text or " const " in text or " let " in text:
        return "arrow const" if "=>" in text else "variable"
    return "other"


def contains(outer, inner):
    return (outer[0], outer[1]) <= (inner[0], inner[1]) and \
           (inner[2], inner[3]) <= (outer[2], outer[3])


def report(index_path, source_root):
    totals = defaultdict(lambda: [0, 0])
    for doc in read_index(index_path):
        if doc["path"].endswith(".d.ts"):
            # A declaration file has no bodies to chunk, and the one in this
            # fixture only exists so the JSX compiles without node_modules.
            print("\n=== %s: skipped, declaration file" % doc["path"])
            continue
        hover = {s["symbol"]: s["documentation"] for s in doc["symbols"]}
        definitions = [o for o in doc["occurrences"] if o["roles"] & DEFINITION_ROLE]
        carried = [o for o in definitions if o["enclosing"]]
        print("\n=== %s: %d definitions, %d with enclosing_range (%d%%)" % (
            doc["path"], len(definitions), len(carried),
            100 * len(carried) // max(len(definitions), 1)))

        source = os.path.join(source_root, doc["path"].replace("\\", os.sep))
        lines = open(source, encoding="utf-8").read().split("\n") \
            if os.path.exists(source) else []
        bodies = []
        for occ in sorted(definitions, key=lambda o: span(o["range"])):
            kind = classify(occ["symbol"], hover.get(occ["symbol"], []))
            totals[kind][0] += 1
            name = span(occ["range"])
            short = occ["symbol"].split("/")[-1][:30]
            if not occ["enclosing"]:
                print("    %-9s L%-4d %-30s enclosing_range absent" % (
                    kind, name[0] + 1, short))
                continue
            totals[kind][1] += 1
            body = span(occ["enclosing"])
            bodies.append((body, occ["symbol"], kind))
            note = "" if contains(body, name) else "  [name outside the body span]"
            whole = bool(lines) and body[0] == 0 and body[2] >= len(lines) - 1
            print("    %-9s L%-4d %-30s body L%d-L%d%s%s" % (
                kind, name[0] + 1, short, body[0] + 1, body[2] + 1,
                "  [whole file]" if whole else "", note))

        for outer, outer_symbol, _ in bodies:
            for inner, inner_symbol, _ in bodies:
                if outer == inner or not contains(outer, inner):
                    continue
                verdict = "ok" if not contains(inner, outer) else "SWALLOWS ITS PARENT"
                print("    nesting: %s L%d-L%d contains %s L%d-L%d -> %s" % (
                    outer_symbol.split("/")[-1][:24], outer[0] + 1, outer[2] + 1,
                    inner_symbol.split("/")[-1][:24], inner[0] + 1, inner[2] + 1,
                    verdict))

        if lines:
            covered = set()
            for body, _, _ in bodies:
                if body[0] == 0 and body[2] >= len(lines) - 1:
                    continue
                covered.update(range(body[0], body[2] + 1))
            outside = [i + 1 for i in range(len(lines))
                       if i not in covered and lines[i].strip()]
            print("    non-blank lines no definition body covers: %s" % outside)

    print("\n%-12s %6s %10s" % ("kind", "defs", "with body"))
    for kind in sorted(totals):
        count, carried = totals[kind]
        print("%-12s %6d %6d %3d%%" % (kind, count, carried, 100 * carried // count))
    overall = [sum(v[0] for v in totals.values()), sum(v[1] for v in totals.values())]
    print("%-12s %6d %6d %3d%%" % ("TOTAL", overall[0], overall[1],
                                   100 * overall[1] // max(overall[0], 1)))


if __name__ == "__main__":
    report(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else ".")
