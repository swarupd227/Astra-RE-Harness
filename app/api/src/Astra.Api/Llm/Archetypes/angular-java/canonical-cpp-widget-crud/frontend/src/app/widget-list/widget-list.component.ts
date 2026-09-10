import { CommonModule } from '@angular/common';
import { Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Widget } from '../models/widget';
import { WidgetService } from '../widget.service';

@Component({
  selector: 'app-widget-list',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './widget-list.component.html',
  styleUrl: './widget-list.component.css',
})
export class WidgetListComponent implements OnInit {
  widgets: Widget[] = [];
  newName = '';
  newQuantity = 0;
  errorMessage: string | null = null;

  constructor(private readonly widgetService: WidgetService) {}

  ngOnInit(): void {
    this.reload();
  }

  reload(): void {
    this.widgetService.list().subscribe({
      next: (widgets) => (this.widgets = widgets),
      error: () => (this.errorMessage = 'Could not load widgets.'),
    });
  }

  /** @SpecClaim EC-1 — a blank name is rejected client-side before it ever reaches the API. */
  add(): void {
    const name = this.newName.trim();
    if (!name) return;
    this.widgetService.create({ name, quantity: this.newQuantity }).subscribe({
      next: () => {
        this.newName = '';
        this.newQuantity = 0;
        this.reload();
      },
      error: () => (this.errorMessage = 'Could not add widget.'),
    });
  }

  delete(id: number): void {
    this.widgetService.remove(id).subscribe({
      next: () => this.reload(),
      error: () => (this.errorMessage = 'Could not delete widget.'),
    });
  }
}
