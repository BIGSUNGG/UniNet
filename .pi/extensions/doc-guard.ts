/**
 * doc-guard — UniNet 문서 동기화 감시 훅
 *
 * agent_settled 시점에 git 상태를 검사해 "코드는 바뀌었는데 Document/ 문서는
 * 그대로"인 상태를 잡아낸다. 경고 표시 + follow-up 재촉(최대 2회).
 * 대화형(tui) 세션에서만 작동 — 서브에이전트/헤드리스 자식은 재촉하지 않는다.
 * 문서 규칙의 진짜 내용은 AGENTS.md와 .pi/skills/doc-sync 를 참고할 것.
 *
 * 주: 이 저장소는 node 프로젝트가 아니므로 @types가 없다. pi 런타임(jiti)이
 * 모듈 로딩과 실행을 책임지고, 여기서는 최소 구조 타입만 로컬로 정의한다.
 */

/* jiti(CJS) 런타임이 제공하는 require — 타입 서버를 위한 로컬 선언 */
declare function require(id: string): unknown;

const { execSync } = require("node:child_process") as {
	execSync: (
		command: string,
		options: {
			cwd: string;
			encoding: string;
			stdio: (string | number)[];
			timeout: number;
		},
	) => string;
};

interface DocGuardUI {
	setStatus: (key: string, text: string) => void;
	notify: (text: string, level: "info" | "error") => void;
}

interface DocGuardCtx {
	mode: "tui" | "rpc" | "json" | "print";
	cwd: string;
	ui: DocGuardUI;
}

interface ToolExecutionEndEvent {
	toolName: string;
	isError?: boolean;
}

interface DocGuardPi {
	on: (
		event: string,
		handler: (event: never, ctx: DocGuardCtx) => void | Promise<void>,
	) => void;
	sendUserMessage: (text: string, opts?: { deliverAs?: string }) => unknown;
}

const DOC_DIR = "Document/";
const MAX_NUDGES = 2;
// 이 경로의 변경은 "코드 변경"으로 세지 않는다 (하네스 설정·메타 파일)
const IGNORED_PREFIXES = [
	".pi",
	".pi-",
	".obsidian",
	".git",
	"Document/",
	".gitignore",
	".gitattributes",
	"LICENSE",
];

function gitChangedPaths(cwd: string): string[] {
	try {
		const out = execSync("git status --porcelain", {
			cwd,
			encoding: "utf8",
			stdio: ["ignore", "pipe", "pipe"],
			timeout: 5000,
		});
		const rows = out.split(/\r?\n/);
		const paths: string[] = [];
		for (const line of rows) {
			if (line) paths.push(line.slice(3).trim().replace(/^"|"$/g, ""));
		}
		return paths;
	} catch {
		return []; // git 없음/오류 → 조용히 통과
	}
}

function classify(paths: string[]): { source: boolean; docs: boolean } {
	let source = false;
	let docs = false;
	for (const p of paths) {
		if (p.startsWith(DOC_DIR)) {
			docs = true;
			continue;
		}
		if (IGNORED_PREFIXES.some((x) => p.startsWith(x))) continue;
		if (p.endsWith(".md")) continue; // 루트 마크다운(AGENTS.md 등)은 코드 아님
		source = true;
	}
	return { source, docs };
}

export default function (pi: DocGuardPi) {
	let nudges = 0;
	let sessionMutated = false; // 이 세션이 edit/write 도구를 썼는가 (재촉 게이트)

	pi.on("tool_execution_end", (event: unknown) => {
		const e = event as ToolExecutionEndEvent;
		if (!e.isError && (e.toolName === "edit" || e.toolName === "write")) {
			sessionMutated = true;
		}
	});

	pi.on("agent_settled", (_event, ctx) => {
		const interactive = ctx.mode === "tui";
		const { source, docs } = classify(gitChangedPaths(ctx.cwd));

		if (!source || docs) {
			nudges = 0; // 정상 상태로 돌아오면 재촉 카운터 리셋
			if (interactive) ctx.ui.setStatus("doc-guard", "");
			return;
		}

		// 코드 변경이 있는데 Document/ 미갱신
		if (interactive) {
			ctx.ui.setStatus(
				"doc-guard",
				"문서 미갱신: 코드 변경 있음, Document/ 갱신 없음",
			);
			if (!sessionMutated) return; // 이 세션이 고친 게 아니면(사용자 직접 수정 등) 표시만
			if (nudges >= MAX_NUDGES) return; // 재촉 한도 초과 — 상태 표시만 유지
			nudges++;
			ctx.ui.notify(
				`[doc-guard] 코드가 변경되었지만 Document/ 문서가 갱신되지 않았습니다 (${nudges}/${MAX_NUDGES})`,
				"error",
			);
			pi.sendUserMessage(
				"[doc-guard] 이 세션에서 코드를 변경했지만 Document/ 문서가 갱신되지 않았습니다. " +
					"doc-sync 스킬에 따라 관련 문서(기능 문서, changelog 등)를 갱신하거나, " +
					"갱신이 불필요한 변경이라면 그 이유를 사용자에게 명시적으로 보고하세요.",
				{ deliverAs: "followUp" },
			);
		}
	});

	pi.on("session_shutdown", () => {
		nudges = 0;
		sessionMutated = false;
	});
}
