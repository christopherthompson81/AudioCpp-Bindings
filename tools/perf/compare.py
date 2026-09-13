import re, sys, difflib

def norm(p):
    # Vernacula's txt export interleaves "[speaker_1] 00:00:00 - 00:00:08" headers
    # with the text. They are not transcript content, so drop them before
    # comparing -- otherwise every timestamp counts as a run of numeric "words".
    with open(p, encoding="utf-8-sig") as fh:
        lines = fh.read().splitlines()
    kept = [l for l in lines if not re.match(r"^\s*\[[^\]]+\]\s*[\d:]+\s*-\s*[\d:]+\s*$", l)]
    t = " ".join(kept).lower()
    t = re.sub(r"[^a-z0-9' ]+", " ", t)
    return t.split()

a_path, b_path, a_name, b_name = sys.argv[1:5]
a, b = norm(a_path), norm(b_path)
if not a or not b:
    # An empty transcript is the answer the caller needs right then -- a run that
    # produced nothing -- not a traceback from dividing by its length.
    print(f"{a_name}: {len(a)} words\n{b_name}: {len(b)} words")
    print("one side is empty: 0% agreement, 100% divergence")
    sys.exit(1)
sm = difflib.SequenceMatcher(a=a, b=b, autojunk=False)
ops = sm.get_opcodes()
eq = sum(i2-i1 for tag,i1,i2,_,_ in ops if tag=="equal")
sub = sum(max(i2-i1, j2-j1) for tag,i1,i2,j1,j2 in ops if tag=="replace")
dele = sum(i2-i1 for tag,i1,i2,_,_ in ops if tag=="delete")
ins  = sum(j2-j1 for tag,_,_,j1,j2 in ops if tag=="insert")

print(f"{a_name}: {len(a)} words")
print(f"{b_name}: {len(b)} words")
print(f"matched {eq}  substituted {sub}  only-in-{a_name} {dele}  only-in-{b_name} {ins}")
print(f"agreement = {eq/max(len(a),len(b))*100:.1f}%   divergence (WER-style vs {a_name}) = {(sub+dele+ins)/len(a)*100:.1f}%")
print()
print("Largest divergences:")
diffs = sorted(((max(i2-i1, j2-j1), tag, i1,i2,j1,j2) for tag,i1,i2,j1,j2 in ops if tag!="equal"),
               reverse=True)[:8]
for size, tag, i1,i2,j1,j2 in diffs:
    print(f"  [{tag} {size}w @word {i1}]")
    print(f"    {a_name}: ...{' '.join(a[max(0,i1-6):i1])} << {' '.join(a[i1:i2])[:130]} >> {' '.join(a[i2:i2+6])}...")
    print(f"    {b_name}: ...{' '.join(b[max(0,j1-6):j1])} << {' '.join(b[j1:j2])[:130]} >> {' '.join(b[j2:j2+6])}...")
