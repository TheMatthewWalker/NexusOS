export type CoreState = "idle" | "typing" | "thinking" | "responding";
export default function NebulaCore({
  state,
  reduced,
  onClick,
}: {
  state: CoreState;
  reduced: boolean;
  onClick: () => void;
}) {
  return (
    <button
      className={`nexus-core pearl-core ${state} ${reduced ? "still" : ""}`}
      onClick={onClick}
      aria-label="Toggle all apps around Nexus AI"
    >
      <span className="core-halo" />
      <img
        className="pearl-sphere"
        src="/manus-storage/nexus-pearl_2f80ec26.png"
        onError={event => {
          if (!event.currentTarget.src.endsWith("/assets/nexus-pearl.png"))
            event.currentTarget.src = "/assets/nexus-pearl.png";
        }}
        alt=""
        draggable={false}
      />
      <span className="pearl-light" />
    </button>
  );
}
