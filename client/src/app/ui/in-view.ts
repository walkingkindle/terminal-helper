import { afterNextRender, DestroyRef, Directive, ElementRef, inject, input, output } from '@angular/core';

/**
 * Emits while the element is within `inViewMargin` of the scroll container's viewport.
 * Used as the infinite-scroll sentinel at the end of the board: after each emit it
 * re-observes, so it keeps loading pages until the sentinel is out of range again.
 */
@Directive({ selector: '[appInView]' })
export class InView {
  readonly appInView = output<void>();
  readonly inViewRoot = input<Element | null>(null);
  readonly inViewMargin = input('600px');

  constructor() {
    const element = inject<ElementRef<Element>>(ElementRef).nativeElement;
    const destroyRef = inject(DestroyRef);

    afterNextRender(() => {
      let destroyed = false;
      const observer = new IntersectionObserver(
        (entries) => {
          if (!entries.some((e) => e.isIntersecting)) return;
          this.appInView.emit();
          observer.unobserve(element);
          requestAnimationFrame(() => destroyed || observer.observe(element));
        },
        { root: this.inViewRoot(), rootMargin: this.inViewMargin() },
      );
      observer.observe(element);
      destroyRef.onDestroy(() => {
        destroyed = true;
        observer.disconnect();
      });
    });
  }
}
