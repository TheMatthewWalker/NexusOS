import { describe, expect, it } from 'vitest';
import { clampRect, magneticTarget, tileRect, workspace } from './windowing';
describe('SpaceUI magnetic windows',()=>{
 it('maps edges and corners to stable tiles',()=>{expect(magneticTarget(0,300,1440,960)).toBe('left');expect(magneticTarget(1439,100,1440,960)).toBe('top-right');expect(magneticTarget(1439,900,1440,960)).toBe('bottom-right');expect(magneticTarget(700,60,1440,960)).toBe('full');expect(magneticTarget(700,300,1440,960)).toBe(null);});
 it('paired resizing preserves one shared divider and a gap',()=>{const a=workspace(1440,960);const l=tileRect('left',1440,960,.62),r=tileRect('right',1440,960,.62);expect(r.x-l.x-l.width).toBeCloseTo(12);expect(l.width+r.width+12).toBe(a.width);expect(r.x+r.width).toBe(a.x+a.width);});
 it('focus tiles partition the right column',()=>{const a=tileRect('top-right',1440,960),b=tileRect('bottom-right',1440,960);expect(a.x).toBe(b.x);expect(b.y-a.y-a.height).toBe(12);});
 it('thirds preserve equal width and workspace bounds',()=>{const a=tileRect('third-left',1440,960),b=tileRect('third-center',1440,960),c=tileRect('third-right',1440,960);expect(a.width).toBe(b.width);expect(c.width).toBe(b.width);expect(c.x+c.width).toBeCloseTo(workspace(1440,960).x+workspace(1440,960).width);});
 it('floating windows remain on screen even after viewport shrink',()=>{const r=clampRect({x:1500,y:1000,width:950,height:650},390,844);expect(r.x).toBeGreaterThanOrEqual(14);expect(r.x+r.width).toBeLessThanOrEqual(376);expect(r.y+r.height).toBeLessThanOrEqual(748);});
});
