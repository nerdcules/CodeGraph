package org.acme.shop;

import java.util.Map;

public class StandardPricing implements Pricing {
    private final Map<String, Long> catalogue;

    public StandardPricing(Map<String, Long> catalogue) {
        this.catalogue = catalogue;
    }

    @Override
    public long price(String sku, int quantity) {
        return unitPrice(sku) * quantity;
    }

    protected long unitPrice(String sku) {
        return Math.abs(catalogue.getOrDefault(sku, 0L));
    }
}
