"""The large model named as on Hugging Face (repo and quantisation): which file
is fetched, checked against the repository's own checksums, and that a version
too big for the computer is not downloaded. Nothing is fetched here."""
import json
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts" / "images"))
import nexus_image_model as m  # noqa: E402

GIB = 1024 ** 3
REPO = "DavidAU/OpenAi-GPT-oss-20b-HERETIC-uncensored-NEO-Imatrix-gguf"
A, B, C = "a" * 64, "b" * 64, "c" * 64


def lfs(path, sha, size):
    return {"type": "file", "path": path, "size": size, "lfs": {"oid": sha, "size": size}}


TREE = [
    {"type": "file", "path": "README.md", "size": 900},
    lfs("OpenAI-20B-NEO-HRR-Q8_0.gguf", A, 22 * GIB),
    lfs("OpenAI-20B-NEO-HRR-Q5_1.gguf", B, 16 * GIB),
    lfs("OpenAI-20B-NEO-HRR-IQ4_NL.gguf", C, 11 * GIB),
    lfs("mmproj-Q8_0.gguf", "d" * 64, GIB),
    lfs("../evil-Q8_0.gguf", "e" * 64, GIB),
]


class FakeApi:
    def __init__(self, tree=TREE, manifest=None, smi=None):
        self.tree, self.manifest, self.smi, self.calls = tree, manifest, smi, []

    def __call__(self, command):
        self.calls.append(command)
        if command[0] == "nvidia-smi":
            return (0, self.smi) if self.smi else (127, "")
        url = command[-1]
        if "/tree/main" in url:
            return (0, json.dumps(self.tree)) if self.tree is not None else (22, "")
        if "/manifests/" in url:
            return (0, json.dumps(self.manifest)) if self.manifest else (22, "")
        return 22, ""


class ResolveTests(unittest.TestCase):
    def test_what_fits(self):
        # 16 GB, no graphics card: what is left beside the desktop.
        self.assertEqual(m.fit_bytes(15 * GIB, 0), 11 * GIB)
        # A built-in GPU's carve-out is not a graphics card.
        self.assertEqual(m.fit_bytes(15 * GIB, 2 * GIB), 11 * GIB)
        # 16 GB with an 8 GB card: the card takes part of the model.
        self.assertEqual(m.fit_bytes(15 * GIB, 8 * GIB), 11 * GIB + 8 * GIB - m.GPU_RESERVE)

    def test_the_quantisation_asked_for_is_used_when_it_fits(self):
        found = m.resolve_hf(REPO, "Q8_0", 40 * GIB, FakeApi())
        self.assertEqual(found["url"], f"https://huggingface.co/{REPO}/resolve/main/OpenAI-20B-NEO-HRR-Q8_0.gguf")
        self.assertEqual((found["sha256"], found["size"], found["note"]), (A, 22 * GIB, ""))

    def test_too_big_for_this_computer_takes_the_biggest_that_fits_and_says_so(self):
        # 16 GB and an 8 GB card: 22 GB cannot be held, 16 GB can.
        found = m.resolve_hf(REPO, "Q8_0", m.fit_bytes(15 * GIB, 8 * GIB), FakeApi())
        self.assertEqual((found["file"], found["sha256"]), ("OpenAI-20B-NEO-HRR-Q5_1.gguf", B))
        self.assertIn("Q8_0 is 23.6 GB", found["note"])
        self.assertIn("biggest version that fits", found["note"])
        # 16 GB and no card: the 11 GiB one.
        self.assertEqual(m.resolve_hf(REPO, "Q8_0", m.fit_bytes(15 * GIB, 0), FakeApi())["file"], "OpenAI-20B-NEO-HRR-IQ4_NL.gguf")
        # Asked for exactly: nothing smaller is taken.
        self.assertIn("more than this computer can hold", m.resolve_hf(REPO, "Q8_0", 12 * GIB, FakeApi(), allow_smaller=False)["error"])
        # Nothing fits at all.
        self.assertIn("nothing smaller that fits", m.resolve_hf(REPO, "Q8_0", 4 * GIB, FakeApi())["error"])

    def test_llama_cpps_own_answer_for_the_tag_wins_when_it_is_in_the_file_list(self):
        api = FakeApi(manifest={"ggufFile": {"rfilename": "OpenAI-20B-NEO-HRR-Q5_1.gguf"}})
        self.assertEqual(m.resolve_hf(REPO, "Q8_0", 40 * GIB, api)["sha256"], B)
        # A name that is not a listed model file is not trusted.
        api = FakeApi(manifest={"ggufFile": {"rfilename": "../../etc/passwd"}})
        self.assertEqual(m.resolve_hf(REPO, "Q8_0", 40 * GIB, api)["sha256"], A)

    def test_bad_names_missing_lists_and_missing_files_are_errors(self):
        self.assertIn("error", m.resolve_hf("not a repo", "Q8_0", GIB, FakeApi()))
        self.assertIn("error", m.resolve_hf(REPO, "Q8;rm", GIB, FakeApi()))
        self.assertIn("file list", m.resolve_hf(REPO, "Q8_0", GIB, FakeApi(tree=None))["error"])
        self.assertIn("no Q2_K file", m.resolve_hf(REPO, "Q2_K", 40 * GIB, FakeApi())["error"])
        # Projector files and odd paths are never model files.
        only = FakeApi(tree=[lfs("mmproj-Q8_0.gguf", A, GIB), lfs("../evil-Q8_0.gguf", B, GIB)])
        self.assertIn("lists no model files", m.resolve_hf(REPO, "Q8_0", 40 * GIB, only)["error"])

    def test_the_choice_is_remembered_and_the_graphics_memory_is_read(self):
        with tempfile.TemporaryDirectory() as directory:
            api = FakeApi(smi="8192\n")
            first = m.resolved_for(directory, REPO, "Q8_0", True, api, memory=15 * GIB)
            self.assertEqual(first["file"], "OpenAI-20B-NEO-HRR-Q5_1.gguf")
            calls = len(api.calls)
            self.assertEqual(m.resolved_for(directory, REPO, "Q8_0", True, api, memory=15 * GIB)["sha256"], B)
            self.assertEqual(len(api.calls), calls)                    # not asked again
            # A different quantisation asked for is looked up afresh.
            self.assertEqual(m.resolved_for(directory, REPO, "IQ4_NL", True, api, memory=15 * GIB)["sha256"], C)

    def test_the_module_asks_for_the_model_by_repository_and_quantisation(self):
        module = (ROOT / "nix/modules/large-model.nix").read_text()
        self.assertIn(f'default = "{REPO}";', module)
        self.assertIn('default = "Q8_0";', module)
        self.assertIn('"--hf-repo" cfg.repo "--hf-quant" cfg.quant', module)


if __name__ == "__main__":
    unittest.main()
