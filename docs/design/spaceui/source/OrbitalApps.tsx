import { useEffect, useState } from "react";
import { AnimatePresence, motion, useIsPresent } from "framer-motion";
import {
  Folder,
  Globe2,
  TerminalSquare,
  Layers3,
  Box,
  Settings2,
} from "lucide-react";
import { APP_IDS, type AppId } from "@shared/nexus";

export const appMeta = {
  files: {
    label: "Files",
    icon: Folder,
    color: "blue",
    detail: "Documents & projects",
  },
  browser: {
    label: "Browser",
    icon: Globe2,
    color: "blue",
    detail: "Explore & research",
  },
  terminal: {
    label: "Terminal",
    icon: TerminalSquare,
    color: "slate",
    detail: "Commands & code",
  },
  studio: {
    label: "Studio",
    icon: Layers3,
    color: "peach",
    detail: "Make something new",
  },
  models: {
    label: "Models",
    icon: Box,
    color: "purple",
    detail: "Local intelligence",
  },
  settings: {
    label: "Settings",
    icon: Settings2,
    color: "mint",
    detail: "Your system, your way",
  },
};
const angles = [-125, -55, -180, 0, 125, 55];
const radius = 211;

function OrbitalApp({
  id,
  reduced,
  onSelect,
}: {
  id: AppId;
  reduced: boolean;
  onSelect: (id: AppId) => void;
}) {
  const present = useIsPresent();
  const [settled, setSettled] = useState(reduced);
  useEffect(() => {
    if (reduced) setSettled(true);
  }, [reduced]);
  const index = APP_IDS.indexOf(id),
    angle = (angles[index] * Math.PI) / 180;
  const x = radius * Math.cos(angle),
    y = radius * Math.sin(angle);
  const bendX = x * 0.46 - y * 0.24,
    bendY = y * 0.46 + x * 0.24;
  const { label, icon: Icon, color, detail } = appMeta[id];
  const interactive = present && settled;
  return (
    <motion.div
      className="orbital-node"
      data-app={id}
      data-settled={interactive}
      style={{ pointerEvents: interactive ? "auto" : "none" }}
      initial={
        reduced
          ? { x, y, scale: 1, opacity: 0 }
          : { x: 0, y: 0, scale: 0.08, opacity: 0, filter: "blur(5px)" }
      }
      animate={{
        x: reduced ? x : [0, bendX, x],
        y: reduced ? y : [0, bendY, y],
        scale: reduced ? 1 : [0.08, 0.62, 1],
        opacity: 1,
        filter: "blur(0px)",
      }}
      exit={
        reduced
          ? { x, y, scale: 1, opacity: 0, transition: { duration: 0.12 } }
          : {
              x: [x, bendX, 0],
              y: [y, bendY, 0],
              scale: 0.08,
              opacity: 0,
              filter: "blur(4px)",
              transition: { duration: 0.6, ease: [0.45, 0, 0.65, 1] },
            }
      }
      onAnimationComplete={() => {
        if (present) setSettled(true);
      }}
      transition={{
        duration: reduced ? 0.12 : 0.85,
        delay: reduced ? 0 : index * 0.035,
        ease: [0.18, 0.8, 0.25, 1],
        times: [0, 0.45, 1],
      }}
    >
      <button
        className={`app-button ${color}`}
        disabled={!interactive}
        onClick={() => onSelect(id)}
        aria-label={`Open ${label} (prototype)`}
        title={detail}
      >
        <Icon strokeWidth={1.65} size={29} />
      </button>
      <span className="app-label">{label}</span>
    </motion.div>
  );
}

export default function OrbitalApps({
  apps,
  reduced,
  onSelect,
}: {
  apps: AppId[];
  reduced: boolean;
  onSelect: (id: AppId) => void;
}) {
  return (
    <>
      <svg className="orbit-lines" viewBox="0 0 600 470" aria-hidden="true">
        <circle cx="300" cy="235" r="211" />
        <circle cx="300" cy="235" r="156" className="inner-orbit" />
        {angles.map((angle, i) => (
          <circle
            key={angle}
            cx={300 + radius * Math.cos((angle * Math.PI) / 180)}
            cy={235 + radius * Math.sin((angle * Math.PI) / 180)}
            r="2.5"
            className={`orbit-dot ${apps.includes(APP_IDS[i]) ? "lit" : ""}`}
          />
        ))}
      </svg>
      <AnimatePresence>
        {APP_IDS.filter(id => apps.includes(id)).map(id => (
          <OrbitalApp key={id} id={id} reduced={reduced} onSelect={onSelect} />
        ))}
      </AnimatePresence>
    </>
  );
}
