package org.acme.shop;

import java.util.ArrayList;
import java.util.List;

public class Cart {
    private final Pricing pricing;
    private final List<String> lines = new ArrayList<>();

    public Cart(Pricing pricing) {
        this.pricing = pricing;
    }

    public void add(String sku) {
        lines.add(sku);
    }

    public long total() {
        long sum = 0;
        for (String sku : lines) {
            sum += pricing.price(sku, 1);
        }
        return sum;
    }

    static int unused() {
        return 42;
    }
}
