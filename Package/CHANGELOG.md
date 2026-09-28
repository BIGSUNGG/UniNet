# UniNet Changelog

상세 변경 기록: 저장소 루트 `Document/changelog.md` (Obsidian Vault).

## 0.1.0

- 초기 패키지 구성 — UniNet.Core / UniNet.Unity 어셈블리
- 기반 스택(DRPC 3.5.0 · MessageProtocol 3.2.0 · Communication 2.7.0 · LiteNetLib 2.1.4 · BouncyCastle 2.7.0) netstandard2.1 DLL 패키지 동봉 (ADR-0021)
- 소스 생성기 3종(DRPC · MessageProtocol · UniNet.CodeGenerator 0.2.1) RoslynAnalyzer 라벨 동봉
- P1~P4 기능: RPC(4종) · [Replicated] 변수 복제(델타) · 동적 스폰/파괴 · 고급 리플리케이션 정책 · NetworkTransform · P4 훅
